using Stockhog.Infrastructure.Lean.Engine.Contracts;
using QuantConnect.Configuration;
using QuantConnect.Lean.Engine;
using QuantConnect.Logging;
using QuantConnect.Packets;
using QuantConnect.Util;

namespace Stockhog.EngineRunner;

/// <summary>
/// Runs one Lean backtest described by a <see cref="BacktestJob"/> file and reports over stdout (<see cref="EngineProtocol"/>).
/// Exit code 0 = completed, 1 = failed, 2 = invalid arguments.
/// </summary>
internal static class Program
{
    private const string AlgorithmAssembly = "Stockhog.Strategies.dll";

    private static async Task<int> Main(string[] args)
    {
        int exitCode;
        if (args.Length != 1 || !File.Exists(args[0]))
        {
            ProtocolWriter.WriteLine(EngineProtocol.Error("Usage: Stockhog.EngineRunner <job-file.json>"));
            exitCode = 2;
        }
        else
        {
            exitCode = await RunAsync(args[0]);
        }

        // Lean leaves foreground threads behind, so the process has to be ended explicitly.
        Environment.Exit(exitCode);
        return exitCode;
    }

    private static async Task<int> RunAsync(string jobPath)
    {
        try
        {
            var job = await BacktestJob.LoadAsync(jobPath);
            var messaging = new StdoutMessagingHandler();

            RunBacktest(job, messaging);

            if (messaging.RuntimeError is not null)
                return 1;

            ProtocolWriter.WriteLine(EngineProtocol.Completed(job.RunId));
            return 0;
        }
        catch (Exception ex)
        {
            ProtocolWriter.WriteLine(EngineProtocol.Error(ex.Message));
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static void RunBacktest(BacktestJob job, StdoutMessagingHandler messaging)
    {
        Log.LogHandler = new ConsoleLogHandler();

        var algorithmLocation = Path.Combine(AppContext.BaseDirectory, AlgorithmAssembly);
        Directory.CreateDirectory(job.ResultsFolder);

        Config.Set("environment", "backtesting");
        Config.Set("algorithm-type-name", job.StrategyName);
        Config.Set("algorithm-location", algorithmLocation);
        Config.Set("data-folder", job.DataFolder);
        Config.Set("result-handler", "QuantConnect.Lean.Engine.Results.BacktestingResultHandler");
        Config.Set("results-destination-folder", job.ResultsFolder);

        // Take the configured job queue and API, but plug in our own messaging (progress over stdout) and lean manager.
        var defaults = LeanEngineSystemHandlers.FromConfiguration(Composer.Instance);
        using var systemHandlers = new LeanEngineSystemHandlers(defaults.JobQueue, defaults.Api, messaging, new NullLeanManager());
        systemHandlers.Initialize();

        var algorithmHandlers = LeanEngineAlgorithmHandlers.FromConfiguration(Composer.Instance);

        var packet = systemHandlers.JobQueue.NextJob(out var algorithmPath);
        if (packet is not BacktestNodePacket backtestPacket)
            throw new InvalidOperationException($"Expected a backtest job, got {packet?.GetType().Name ?? "null"}.");

        // Lean names the result file after the backtest id: <ResultsFolder>/<RunId>.json
        backtestPacket.BacktestId = job.RunId;
        backtestPacket.Parameters = job.ToAlgorithmParameters();

        if (string.IsNullOrEmpty(algorithmPath) || !File.Exists(algorithmPath))
            algorithmPath = algorithmLocation;

        var algorithmManager = new AlgorithmManager(liveMode: false, backtestPacket);
        var engine = new Engine(systemHandlers, algorithmHandlers, liveMode: false);

        using var workerThread = (WorkerThread)Activator.CreateInstance(typeof(WorkerThread), nonPublic: true)!;
        engine.Run(backtestPacket, algorithmManager, algorithmPath, workerThread);
    }
}
