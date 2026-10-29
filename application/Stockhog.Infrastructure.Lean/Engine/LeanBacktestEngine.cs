using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Stockhog.Application.Backtesting;
using Stockhog.Infrastructure.Configuration;
using Stockhog.Infrastructure.Lean.Engine.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Stockhog.Infrastructure.Lean.Engine;

/// <summary>
/// Runs each backtest in a separate Stockhog.EngineRunner process. Lean keeps its configuration in static state,
/// so a process per run is what lets several backtests run at the same time.
/// </summary>
internal sealed class LeanBacktestEngine(
    IOptions<StorageOptions> storage,
    IOptions<LeanOptions> leanOptions,
    ILogger<LeanBacktestEngine> logger) : IBacktestEngine
{
    private const string RunnerExecutable = "Stockhog.EngineRunner.exe";

#if DEBUG
    private const string BuildConfiguration = "Debug";
#else
    private const string BuildConfiguration = "Release";
#endif

    public async Task RunAsync(
        BacktestRequest request,
        IProgress<BacktestProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var runnerPath = ResolveRunnerPath();
        if (!File.Exists(runnerPath))
            throw new BacktestEngineException($"Engine runner not found at '{runnerPath}'. Build the Stockhog.EngineRunner project first.");

        // Value objects guarantee the symbol and run id are safe to use in file paths.
        var runId = request.RunId.Value;
        var resultsFolder = Path.Combine(storage.Value.ResultsFolder, request.Symbol.Value);
        Directory.CreateDirectory(resultsFolder);

        var job = new BacktestJob(
            runId,
            request.Strategy.Value,
            request.Symbol.Value,
            request.Period.Start,
            request.Period.End,
            request.InitialCapital.Amount,
            storage.Value.DataFolder,
            resultsFolder,
            request.Parameters.Values);
        var jobPath = Path.Combine(resultsFolder, $"{runId}-job.json");
        await job.SaveAsync(jobPath, cancellationToken).ConfigureAwait(false);

        progress?.Report(new BacktestProgress(0, "Starting engine"));

        using var process = new Process { StartInfo = CreateStartInfo(runnerPath, jobPath) };
        process.Start();
        logger.LogInformation("Engine process {ProcessId} started for run {RunId}", process.Id, runId);

        var state = new RunState();
        var stdout = PumpAsync(process.StandardOutput, line => HandleOutput(line, runId, state, progress));
        var stderr = PumpAsync(process.StandardError, line => logger.LogWarning("[{RunId}] {Line}", runId, line));

        await using (cancellationToken.Register(() => Kill(process, runId)))
        {
            // Not cancellable on purpose: after a kill we still wait for the exit so no output pump is left running.
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (process.ExitCode != 0 || !state.Completed)
            throw new BacktestEngineException(state.LastError ?? $"Engine exited with code {process.ExitCode}.");

        logger.LogInformation("Run {RunId} completed", runId);
        progress?.Report(new BacktestProgress(100, "Completed"));
    }

    private void HandleOutput(string line, string runId, RunState state, IProgress<BacktestProgress>? progress)
    {
        var message = EngineProtocol.Parse(line);
        switch (message.Kind)
        {
            case EngineMessageKind.Progress:
                state.Percent = message.Percent;
                progress?.Report(new BacktestProgress(message.Percent));
                break;
            case EngineMessageKind.Log:
                logger.LogInformation("[{RunId}] {Message}", runId, message.Text);
                progress?.Report(new BacktestProgress(state.Percent, message.Text));
                break;
            case EngineMessageKind.Error:
                state.LastError = message.Text;
                logger.LogError("[{RunId}] {Message}", runId, message.Text);
                break;
            case EngineMessageKind.Completed:
                state.Completed = true;
                break;
            default:
                logger.LogDebug("[{RunId}] {Line}", runId, message.Text);
                break;
        }
    }

    private static ProcessStartInfo CreateStartInfo(string runnerPath, string jobPath)
    {
        var startInfo = new ProcessStartInfo(runnerPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(runnerPath)!
        };
        startInfo.ArgumentList.Add(jobPath);
        return startInfo;
    }

    /// <summary>
    /// Reads until the process closes the stream. Both streams are drained concurrently so neither pipe buffer can fill up and block the engine.
    /// </summary>
    private static async Task PumpAsync(StreamReader reader, Action<string> onLine)
    {
        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            onLine(line);
    }

    private void Kill(Process process, string runId)
    {
        try
        {
            process.Kill(entireProcessTree: true);
            logger.LogInformation("Run {RunId} cancelled, engine process killed", runId);
        }
        catch (InvalidOperationException)
        {
            // Already exited.
        }
        catch (Win32Exception ex)
        {
            logger.LogWarning(ex, "Could not kill engine process for run {RunId}", runId);
        }
    }

    private string ResolveRunnerPath()
    {
        var configured = leanOptions.Value.EngineRunnerPath;
        if (!string.IsNullOrWhiteSpace(configured))
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(configured));

        var besideApp = Path.Combine(AppContext.BaseDirectory, RunnerExecutable);
        if (File.Exists(besideApp))
            return besideApp;

        // Development: the runner is built into its own project's output folder.
        return Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..",
            "Stockhog.EngineRunner", "bin", BuildConfiguration, "net10.0", RunnerExecutable));
    }

    private sealed class RunState
    {
        public int Percent { get; set; }
        public bool Completed { get; set; }
        public string? LastError { get; set; }
    }
}
