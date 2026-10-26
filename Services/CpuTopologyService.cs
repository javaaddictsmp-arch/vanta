using System.Numerics;
using System.Runtime.InteropServices;
using TuneUp.Models;

namespace TuneUp.Services;

/// <summary>
/// Reads the real core layout from Windows (GetLogicalProcessorInformationEx) instead of
/// guessing from the CPU name:
///  - Intel hybrid: P-cores report a higher EfficiencyClass than E-cores.
///  - AMD dual-CCD X3D: each CCD has its own L3; the V-Cache CCD is the one with the bigger L3.
///  - SMT: a core whose mask has two bits set has two logical processors.
/// Only processor group 0 (up to 64 logical CPUs) is handled, which covers desktop CPUs.
/// </summary>
public sealed class CpuTopology
{
    public int LogicalCount { get; init; }
    public int CoreCount { get; init; }
    public bool HasSmt { get; init; }

    public bool IsHybrid { get; init; }
    public ulong PCoreMask { get; init; }
    public ulong ECoreMask { get; init; }

    /// <summary>One mask per L3 cache, largest cache first.</summary>
    public List<(ulong Mask, long L3Bytes)> L3Groups { get; init; } = new();
    public bool IsDualCcdVCache { get; init; }

    public ulong AllMask { get; init; }
    public ulong OneThreadPerCoreMask { get; init; }

    public string Describe()
    {
        var parts = new List<string> { $"{CoreCount} cores / {LogicalCount} threads" };
        if (IsHybrid)
            parts.Add($"hybrid: {BitOperations.PopCount(PCoreMask)} P-core threads, {BitOperations.PopCount(ECoreMask)} E-cores");
        if (L3Groups.Count > 1)
            parts.Add(IsDualCcdVCache
                ? $"{L3Groups.Count} CCDs, V-Cache CCD has {L3Groups[0].L3Bytes / 1024 / 1024} MB L3"
                : $"{L3Groups.Count} L3 groups (CCDs)");
        parts.Add(HasSmt ? "SMT/Hyper-Threading on" : "no SMT");
        return string.Join(", ", parts);
    }

    /// <summary>The mask for a preset, or null when the preset makes no sense on this CPU.</summary>
    public ulong? MaskFor(AffinityPreset preset) => preset switch
    {
        AffinityPreset.PerformanceCoresOnly when IsHybrid && PCoreMask != 0 => PCoreMask,
        AffinityPreset.VCacheCcdOnly when IsDualCcdVCache => L3Groups[0].Mask,
        AffinityPreset.FrequencyCcdOnly when L3Groups.Count > 1 => L3Groups[^1].Mask,
        AffinityPreset.OneThreadPerCore when HasSmt => OneThreadPerCoreMask,
        _ => null
    };

    public string WhyNot(AffinityPreset preset) => preset switch
    {
        AffinityPreset.PerformanceCoresOnly => "This CPU has no E-cores, so there is nothing to separate.",
        AffinityPreset.VCacheCcdOnly => "This CPU does not have two CCDs with different L3 sizes (only dual-CCD X3D chips like the 7950X3D/9950X3D do).",
        AffinityPreset.FrequencyCcdOnly => "This CPU has a single CCD / L3 cache.",
        AffinityPreset.OneThreadPerCore => "SMT/Hyper-Threading is off or not supported, so every core already runs one thread.",
        _ => ""
    };
}

public static class CpuTopologyService
{
    private static CpuTopology? _cached;

    public static CpuTopology Get() => _cached ??= Read();

    private static CpuTopology Read()
    {
        var cores = new List<(ulong Mask, byte EffClass)>();
        var l3 = new List<(ulong Mask, long Size)>();

        try
        {
            foreach (var (ptr, _) in Enumerate(NativeMethods.RelationProcessorCore))
            {
                // PROCESSOR_RELATIONSHIP at offset 8: Flags(1) EfficiencyClass(1) Reserved(20) GroupCount(2) pad(2) GROUP_AFFINITY...
                var eff = Marshal.ReadByte(ptr, 9);
                var group = (ushort)Marshal.ReadInt16(ptr, 40);
                var mask = (ulong)Marshal.ReadInt64(ptr, 32);
                if (group == 0) cores.Add((mask, eff));
            }

            foreach (var (ptr, _) in Enumerate(NativeMethods.RelationCache))
            {
                // CACHE_RELATIONSHIP at offset 8: Level(1) Assoc(1) LineSize(2) CacheSize(4) Type(4) Reserved(18) GroupCount(2) GROUP_AFFINITY...
                var level = Marshal.ReadByte(ptr, 8);
                var size = (uint)Marshal.ReadInt32(ptr, 12);
                var mask = (ulong)Marshal.ReadInt64(ptr, 40);
                var group = (ushort)Marshal.ReadInt16(ptr, 48);
                if (level == 3 && group == 0) l3.Add((mask, size));
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("Reading CPU topology failed", ex);
        }

        if (cores.Count == 0)
        {
            var n = Math.Min(Environment.ProcessorCount, 64);
            var all = n == 64 ? ulong.MaxValue : (1UL << n) - 1;
            return new CpuTopology { LogicalCount = n, CoreCount = n, AllMask = all, OneThreadPerCoreMask = all };
        }

        var allMask = cores.Aggregate(0UL, (a, c) => a | c.Mask);
        var maxEff = cores.Max(c => c.EffClass);
        var minEff = cores.Min(c => c.EffClass);
        var hybrid = maxEff != minEff;

        var oneThread = cores.Aggregate(0UL, (a, c) => a | LowestBit(c.Mask));
        var l3Sorted = l3.Distinct().OrderByDescending(x => x.Size).ToList();

        return new CpuTopology
        {
            LogicalCount = BitOperations.PopCount(allMask),
            CoreCount = cores.Count,
            HasSmt = cores.Any(c => BitOperations.PopCount(c.Mask) > 1),
            IsHybrid = hybrid,
            PCoreMask = hybrid ? cores.Where(c => c.EffClass == maxEff).Aggregate(0UL, (a, c) => a | c.Mask) : 0,
            ECoreMask = hybrid ? cores.Where(c => c.EffClass != maxEff).Aggregate(0UL, (a, c) => a | c.Mask) : 0,
            L3Groups = l3Sorted,
            IsDualCcdVCache = l3Sorted.Count == 2 && l3Sorted[0].Size >= l3Sorted[1].Size * 2,
            AllMask = allMask,
            OneThreadPerCoreMask = oneThread
        };
    }

    private static ulong LowestBit(ulong v) => v & (~v + 1);

    /// <summary>Yields a pointer to each SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX record.</summary>
    private static IEnumerable<(IntPtr Ptr, int Size)> Enumerate(int relation)
    {
        uint len = 0;
        NativeMethods.GetLogicalProcessorInformationEx(relation, IntPtr.Zero, ref len);
        if (len == 0) yield break;

        var buffer = Marshal.AllocHGlobal((int)len);
        try
        {
            if (!NativeMethods.GetLogicalProcessorInformationEx(relation, buffer, ref len)) yield break;

            var offset = 0;
            while (offset < len)
            {
                var ptr = buffer + offset;
                var size = Marshal.ReadInt32(ptr, 4);
                if (size <= 0) yield break;
                yield return (ptr, size);
                offset += size;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}
