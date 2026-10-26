using System.Globalization;
using Microsoft.Win32;

namespace TuneUp.Services;

/// <summary>
/// Thin wrapper over the 64-bit registry view. Values travel as strings so they can live in
/// JSON (tweak catalog and change log) and be parsed according to their RegistryValueKind.
/// </summary>
public static class RegistryService
{
    public sealed record RegValue(bool Exists, RegistryValueKind Kind, string? Value);

    public static RegistryKey OpenBase(string hive) => hive.ToUpperInvariant() switch
    {
        "HKCU" or "HKEY_CURRENT_USER" => RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64),
        "HKLM" or "HKEY_LOCAL_MACHINE" => RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64),
        _ => throw new ArgumentException($"Unsupported registry hive '{hive}'")
    };

    public static string HiveForRegExe(string hive) => hive.ToUpperInvariant() switch
    {
        "HKCU" or "HKEY_CURRENT_USER" => "HKCU",
        "HKLM" or "HKEY_LOCAL_MACHINE" => "HKLM",
        _ => throw new ArgumentException($"Unsupported registry hive '{hive}'")
    };

    public static bool KeyExists(string hive, string path)
    {
        using var baseKey = OpenBase(hive);
        using var key = baseKey.OpenSubKey(path, false);
        return key != null;
    }

    public static RegValue Read(string hive, string path, string name)
    {
        using var baseKey = OpenBase(hive);
        using var key = baseKey.OpenSubKey(path, false);
        if (key == null) return new RegValue(false, RegistryValueKind.None, null);

        var raw = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (raw == null) return new RegValue(false, RegistryValueKind.None, null);

        var kind = key.GetValueKind(name);
        return new RegValue(true, kind, Serialize(raw, kind));
    }

    public static void Write(string hive, string path, string name, RegistryValueKind kind, string value)
    {
        using var baseKey = OpenBase(hive);
        using var key = baseKey.CreateSubKey(path, true)
                        ?? throw new InvalidOperationException($"Cannot open or create {hive}\\{path}");
        key.SetValue(name, Deserialize(value, kind), kind);
    }

    public static void DeleteValue(string hive, string path, string name)
    {
        using var baseKey = OpenBase(hive);
        using var key = baseKey.OpenSubKey(path, true);
        key?.DeleteValue(name, false);
    }

    /// <summary>True when the current value equals the expected value (type-aware).</summary>
    public static bool Matches(RegValue current, RegistryValueKind expectedKind, string expected)
    {
        if (!current.Exists || current.Value == null) return false;

        // REG_SZ "0" vs REG_DWORD 0 are different things; require same kind family.
        if (current.Kind != expectedKind) return false;

        return expectedKind switch
        {
            RegistryValueKind.DWord => ParseDword(current.Value) == ParseDword(expected),
            RegistryValueKind.QWord => long.Parse(current.Value, CultureInfo.InvariantCulture)
                                       == long.Parse(expected, CultureInfo.InvariantCulture),
            _ => string.Equals(current.Value, expected, StringComparison.OrdinalIgnoreCase)
        };
    }

    // ---- Serialization helpers ---------------------------------------------------------------
    public static string Serialize(object raw, RegistryValueKind kind) => kind switch
    {
        RegistryValueKind.DWord => ((int)raw).ToString(CultureInfo.InvariantCulture),
        RegistryValueKind.QWord => ((long)raw).ToString(CultureInfo.InvariantCulture),
        RegistryValueKind.Binary => Convert.ToBase64String((byte[])raw),
        RegistryValueKind.MultiString => string.Join("\n", (string[])raw),
        _ => raw.ToString() ?? ""
    };

    public static object Deserialize(string value, RegistryValueKind kind) => kind switch
    {
        RegistryValueKind.DWord => ParseDword(value),
        RegistryValueKind.QWord => long.Parse(value, CultureInfo.InvariantCulture),
        RegistryValueKind.Binary => Convert.FromBase64String(value),
        RegistryValueKind.MultiString => value.Split('\n'),
        _ => value
    };

    /// <summary>Accepts decimal ("38"), unsigned ("4294967295") or hex ("0x26").</summary>
    public static int ParseDword(string value)
    {
        value = value.Trim();
        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return unchecked((int)Convert.ToUInt32(value[2..], 16));

        var asLong = long.Parse(value, CultureInfo.InvariantCulture);
        return unchecked((int)asLong); // 4294967295 -> -1 (same bits as 0xFFFFFFFF)
    }
}
