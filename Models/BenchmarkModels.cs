namespace TuneUp.Models;

/// <summary>One frametime capture (an "A" or "B" run).</summary>
public sealed class BenchmarkResult
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
    public string Game { get; set; } = "";
    public string ProcessName { get; set; } = "";
    /// <summary>"A (before)", "B (after)" or your own label.</summary>
    public string Label { get; set; } = "";
    public string Notes { get; set; } = "";

    /// <summary>Where the present events came from: DXGI, D3D9 or DxgKrnl.</summary>
    public string Source { get; set; } = "";

    public double DurationSec { get; set; }
    public int FrameCount { get; set; }

    public double AvgFps { get; set; }
    /// <summary>1000 / 99th-percentile frametime (the "P1" metric CapFrameX calls 1% low).</summary>
    public double Low1Fps { get; set; }
    /// <summary>1000 / 99.9th-percentile frametime.</summary>
    public double Low01Fps { get; set; }
    public double MedianFrametimeMs { get; set; }
    public double MaxFrametimeMs { get; set; }
    /// <summary>Frames that took more than twice the median and at least 8 ms longer.</summary>
    public int StutterCount { get; set; }

    /// <summary>Average FPS per 5-second chunk, used to estimate run-to-run noise.</summary>
    public List<double> ChunkFps { get; set; } = new();

    /// <summary>Raw frametimes in ms (MsBetweenPresents).</summary>
    public List<float> Frametimes { get; set; } = new();

    /// <summary>Bottleneck verdict sampled during the run, if any.</summary>
    public string? Bottleneck { get; set; }
}

public sealed class BenchmarkComparison
{
    public double AvgFpsDiffPct { get; init; }
    public double Low1DiffPct { get; init; }
    public double Low01DiffPct { get; init; }
    public int StutterDiff { get; init; }
    public bool WithinNoise { get; init; }
    public double NoisePct { get; init; }
    public string Verdict { get; init; } = "";
}

public sealed class BottleneckReport
{
    public string Verdict { get; init; } = "";
    public List<string> Evidence { get; init; } = new();
    public List<string> Fixes { get; init; } = new();
    public int SampleCount { get; init; }
}

public sealed class LatencyReport
{
    public DateTime TimestampUtc { get; init; } = DateTime.UtcNow;
    public string Label { get; init; } = "";
    public double DurationSec { get; init; }
    public long DpcCount { get; init; }
    public long IsrCount { get; init; }
    public double MaxDpcUs { get; init; }
    public double MaxIsrUs { get; init; }
    public double P99DpcUs { get; init; }
    public double P99IsrUs { get; init; }
    public string WorstDpcDriver { get; init; } = "";
    public string WorstIsrDriver { get; init; } = "";
    public string Verdict { get; init; } = "";
    public string Error { get; init; } = "";
}
