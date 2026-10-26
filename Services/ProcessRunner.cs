using System.Diagnostics;

namespace TuneUp.Services;

public sealed record ProcResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Ok => ExitCode == 0;
}

/// <summary>Runs a console program without a window and captures its output.</summary>
public static class ProcessRunner
{
    /// <summary>Like RunAsync but reports each output line as it arrives (for SFC/DISM/defrag live output).</summary>
    public static async Task<int> RunStreamingAsync(string fileName, string arguments, Action<string> onLine,
                                                     int timeoutMs, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(fileName, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        try
        {
            using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            proc.OutputDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) onLine(e.Data.Replace("\0", "")); };
            proc.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) onLine(e.Data.Replace("\0", "")); };
            proc.Start();
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeoutMs);
            try
            {
                await proc.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(true); } catch { /* gone */ }
                onLine("Stopped.");
                return -1;
            }
            return proc.ExitCode;
        }
        catch (Exception ex)
        {
            AppLog.Error($"Failed to run {fileName} {arguments}", ex);
            onLine("Could not start: " + ex.Message);
            return -1;
        }
    }

    /// <summary>Runs a PowerShell snippet (used for NetAdapter / Appx / NetQos cmdlets that have no simple API).</summary>
    public static Task<ProcResult> PowerShellAsync(string script, int timeoutMs = 60_000, CancellationToken ct = default)
    {
        var encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(
            "$ProgressPreference='SilentlyContinue'; [Console]::OutputEncoding=[Text.Encoding]::UTF8; " + script));
        return RunAsync("powershell.exe", $"-NoProfile -NonInteractive -EncodedCommand {encoded}", timeoutMs, ct);
    }

    public static async Task<ProcResult> RunAsync(
        string fileName, string arguments, int timeoutMs = 60_000, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(fileName, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        try
        {
            using var proc = new Process { StartInfo = psi };
            proc.Start();

            var stdOutTask = proc.StandardOutput.ReadToEndAsync();
            var stdErrTask = proc.StandardError.ReadToEndAsync();

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeoutMs);

            try
            {
                await proc.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(true); } catch { /* already gone */ }
                return new ProcResult(-1, "", "Timed out or cancelled");
            }

            return new ProcResult(proc.ExitCode, await stdOutTask, await stdErrTask);
        }
        catch (Exception ex)
        {
            AppLog.Error($"Failed to run {fileName} {arguments}", ex);
            return new ProcResult(-1, "", ex.Message);
        }
    }
}
