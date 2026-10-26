using System.Text.Json;
using TuneUp.Models;

namespace TuneUp.Services;

/// <summary>
/// A/B benchmarking with honest statistics. Each run is split into 5-second chunks; the spread
/// of those chunks estimates run-to-run noise. A difference smaller than ~2 standard errors (or
/// under 2%) is reported as "within noise" instead of being sold as an improvement.
/// </summary>
public sealed class BenchmarkService
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    public static BenchmarkResult Analyze(CaptureResult capture, string game, string processName, string label)
    {
        var ft = capture.Frametimes;
        var sorted = ft.Select(x => (double)x).OrderBy(x => x).ToArray();
        var total = sorted.Sum();
        var median = Percentile(sorted, 0.5);

        // Chunk FPS (5 s chunks) for the noise estimate.
        var chunks = new List<double>();
        double acc = 0;
        var frames = 0;
        foreach (var f in ft)
        {
            acc += f;
            frames++;
            if (acc >= 5000)
            {
                chunks.Add(frames * 1000.0 / acc);
                acc = 0;
                frames = 0;
            }
        }

        return new BenchmarkResult
        {
            Game = game,
            ProcessName = processName,
            Label = label,
            Source = capture.Source,
            DurationSec = total / 1000.0,
            FrameCount = ft.Count,
            AvgFps = ft.Count * 1000.0 / total,
            Low1Fps = 1000.0 / Percentile(sorted, 0.99),
            Low01Fps = 1000.0 / Percentile(sorted, 0.999),
            MedianFrametimeMs = median,
            MaxFrametimeMs = sorted[^1],
            StutterCount = ft.Count(f => f > median * 2 && f - median > 8),
            ChunkFps = chunks,
            Frametimes = ft
        };
    }

    private static double Percentile(double[] sorted, double p)
    {
        if (sorted.Length == 0) return 0;
        var idx = Math.Clamp((int)Math.Ceiling(p * sorted.Length) - 1, 0, sorted.Length - 1);
        return sorted[idx];
    }

    public static BenchmarkComparison Compare(BenchmarkResult a, BenchmarkResult b)
    {
        static double Pct(double from, double to) => from <= 0 ? 0 : (to - from) / from * 100.0;

        var avgDiff = Pct(a.AvgFps, b.AvgFps);

        // Standard error of the difference in mean chunk FPS, expressed as % of A.
        double noisePct;
        if (a.ChunkFps.Count >= 3 && b.ChunkFps.Count >= 3)
        {
            var se = Math.Sqrt(Variance(a.ChunkFps) / a.ChunkFps.Count + Variance(b.ChunkFps) / b.ChunkFps.Count);
            noisePct = 2 * se / a.AvgFps * 100.0;
        }
        else
        {
            noisePct = 5; // too short to estimate: assume a typical 5% run-to-run spread
        }
        noisePct = Math.Max(noisePct, 2.0);

        var within = Math.Abs(avgDiff) < noisePct;
        var low1 = Pct(a.Low1Fps, b.Low1Fps);

        string verdict;
        if (within && Math.Abs(low1) < Math.Max(noisePct * 1.5, 4))
            verdict = $"No real difference: the change is within run-to-run noise (about ±{noisePct:0.#}%). " +
                      "Whatever you changed between A and B did not measurably help on this PC in this scene.";
        else if (avgDiff > 0 || low1 > 0)
            verdict = $"B looks better: average FPS {avgDiff:+0.#;-0.#}%, 1% lows {low1:+0.#;-0.#}% (noise about ±{noisePct:0.#}%). " +
                      "Repeat each run once more to be sure.";
        else
            verdict = $"B looks worse: average FPS {avgDiff:+0.#;-0.#}%, 1% lows {low1:+0.#;-0.#}% (noise about ±{noisePct:0.#}%). " +
                      "Consider reverting what you changed.";

        if (a.DurationSec < 30 || b.DurationSec < 30)
            verdict += " Runs shorter than 30 s are noisy; 60 s or more is better.";
        if (!string.Equals(a.ProcessName, b.ProcessName, StringComparison.OrdinalIgnoreCase))
            verdict += " Warning: A and B were captured from different programs.";

        return new BenchmarkComparison
        {
            AvgFpsDiffPct = avgDiff,
            Low1DiffPct = low1,
            Low01DiffPct = Pct(a.Low01Fps, b.Low01Fps),
            StutterDiff = b.StutterCount - a.StutterCount,
            WithinNoise = within,
            NoisePct = noisePct,
            Verdict = verdict
        };
    }

    private static double Variance(IReadOnlyList<double> xs)
    {
        if (xs.Count < 2) return 0;
        var mean = xs.Average();
        return xs.Sum(x => (x - mean) * (x - mean)) / (xs.Count - 1);
    }

    // ---- History ------------------------------------------------------------------------------
    public void Save(BenchmarkResult r)
    {
        try
        {
            File.WriteAllText(Path.Combine(AppPaths.Benchmarks, $"{r.TimestampUtc:yyyyMMdd-HHmmss}-{r.Id:N}.json"),
                JsonSerializer.Serialize(r, Json));
        }
        catch (Exception ex)
        {
            AppLog.Error("Could not save benchmark", ex);
        }
    }

    public List<BenchmarkResult> LoadHistory()
    {
        var list = new List<BenchmarkResult>();
        foreach (var file in Directory.EnumerateFiles(AppPaths.Benchmarks, "*.json"))
        {
            try
            {
                var r = JsonSerializer.Deserialize<BenchmarkResult>(File.ReadAllText(file));
                if (r != null) list.Add(r);
            }
            catch (Exception ex)
            {
                AppLog.Warn($"Skipping benchmark file {file}: {ex.Message}");
            }
        }
        return list.OrderByDescending(r => r.TimestampUtc).ToList();
    }

    public void Delete(BenchmarkResult r)
    {
        foreach (var file in Directory.EnumerateFiles(AppPaths.Benchmarks, $"*{r.Id:N}.json"))
            try { File.Delete(file); } catch { /* ignore */ }
    }
}

/// <summary>
/// Works out what is limiting FPS from sensor samples taken while the game runs. Rules of thumb
/// (the same reasoning reviewers use), always with the evidence shown so you can judge it.
/// </summary>
public static class BottleneckService
{
    public static BottleneckReport Analyze(IReadOnlyList<MetricSample> samples)
    {
        if (samples.Count < 5)
            return new BottleneckReport { Verdict = "Not enough data", Evidence = { "Need at least 5 seconds of sensor samples." } };

        var evidence = new List<string>();
        var fixes = new List<string>();

        var gpu = samples.Where(s => s.GpuLoad.HasValue).Select(s => s.GpuLoad!.Value).ToList();
        var gpuAvg = gpu.Count > 0 ? gpu.Average() : (double?)null;
        var cpuAvg = samples.Average(s => s.CpuLoad);
        var maxThread = samples.Where(s => s.CpuThreadLoads.Length > 0).Select(s => s.CpuThreadLoads.Max()).DefaultIfEmpty(0).Average();
        var ram = samples.Average(s => s.RamPercent);
        var vram = samples.Where(s => s.GpuMemPercent.HasValue).Select(s => s.GpuMemPercent!.Value).DefaultIfEmpty(0).Max();
        var cpuTemp = samples.Where(s => s.CpuTempC.HasValue).Select(s => s.CpuTempC!.Value).DefaultIfEmpty(0).Max();
        var gpuTemp = samples.Where(s => s.GpuTempC.HasValue).Select(s => s.GpuTempC!.Value).DefaultIfEmpty(0).Max();
        var disk = samples.Where(s => s.DiskActivePercent.HasValue).Select(s => s.DiskActivePercent!.Value).DefaultIfEmpty(0).Average();

        if (gpuAvg.HasValue) evidence.Add($"GPU load averaged {gpuAvg:0}%");
        evidence.Add($"CPU total averaged {cpuAvg:0}%, busiest thread averaged {maxThread:0}%");
        evidence.Add($"RAM {ram:0}% used, VRAM peak {vram:0}%");
        if (cpuTemp > 0) evidence.Add($"CPU peak {cpuTemp:0} °C");
        if (gpuTemp > 0) evidence.Add($"GPU peak {gpuTemp:0} °C");

        // GPU clock sagging while load is high and temperature is fine suggests power limit.
        var hot = samples.Where(s => s.GpuLoad > 90 && s.GpuClockMhz.HasValue).Select(s => s.GpuClockMhz!.Value).ToList();
        var clockSag = hot.Count > 5 && hot.Max() > 0 && (hot.Max() - hot.Average()) / hot.Max() > 0.08;

        var verdicts = new List<string>();

        if (cpuTemp >= 95 || gpuTemp >= 87)
        {
            verdicts.Add("Thermal throttling likely");
            fixes.Add("Check cooler mounting and fan curves, clean dust filters, improve case airflow.");
            fixes.Add("A mild undervolt (MSI Afterburner curve for GPUs, Curve Optimizer/PBO for Ryzen) often drops temperatures without losing FPS.");
        }

        if (vram >= 95)
        {
            verdicts.Add("VRAM is full");
            fixes.Add("Lower texture quality one step; that is the setting that uses the most VRAM.");
            fixes.Add("Close browsers/video players that use the GPU while gaming.");
        }

        if (ram >= 90)
        {
            verdicts.Add("System RAM is nearly full");
            fixes.Add("Close memory-heavy background apps (browsers with many tabs, launchers).");
            fixes.Add("16 GB is the practical minimum for current games; 32 GB if you stream or keep a browser open.");
        }

        if (gpuAvg is >= 95)
        {
            verdicts.Add("GPU-bound (this is normal and means the CPU is keeping up)");
            fixes.Add("For more FPS lower GPU-heavy settings: resolution/upscaling (DLSS/FSR/XeSS Quality), shadows, ray tracing, volumetrics.");
            if (clockSag)
            {
                evidence.Add("GPU clock dropped >8% under full load while temperatures were OK");
                verdicts.Add("GPU power limit likely");
                fixes.Add("The card is hitting its power limit. Raising the power limit or a careful undervolt in MSI Afterburner can help; Vanta does not overclock.");
            }
        }
        else if (gpuAvg is < 85 && (maxThread >= 90 || cpuAvg >= 85))
        {
            verdicts.Add("CPU-bound");
            fixes.Add("Lower CPU-heavy settings: view/draw distance, crowd/NPC density, physics, simulation distance (Minecraft).");
            fixes.Add("Enable XMP/EXPO in BIOS if RAM is below its rated speed (see Dashboard); RAM speed matters most when CPU-bound.");
            fixes.Add("Close background apps that use CPU; keep ProBalance on in the boost profile.");
            fixes.Add("If the FPS is already above your monitor's refresh rate, cap it a little below refresh for smoother frametimes.");
        }
        else if (gpuAvg is < 85)
        {
            verdicts.Add("Neither CPU nor GPU is maxed out");
            fixes.Add("You are probably hitting an FPS cap or V-Sync (in game, driver, or RTSS). That is fine if it is intentional.");
            fixes.Add("If not capped: the game may be limited by one engine thread, streaming (disk), or network sync.");
        }

        if (disk >= 90)
        {
            verdicts.Add("Disk was very busy");
            fixes.Add("Stutter while loading new areas can come from a slow drive. Move the game to an SSD (Phase 4 adds a helper).");
        }

        if (!gpuAvg.HasValue)
            evidence.Add("No GPU load sensor available, so GPU-bound detection is limited.");

        return new BottleneckReport
        {
            Verdict = verdicts.Count > 0 ? string.Join(" + ", verdicts) : "No clear bottleneck",
            Evidence = evidence,
            Fixes = fixes.Distinct().ToList(),
            SampleCount = samples.Count
        };
    }
}
