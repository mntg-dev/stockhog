namespace Stockhog.Infrastructure.Lean;

public sealed class LeanOptions
{
    public const string SectionName = "Lean";

    /// <summary>
    /// Full path to Stockhog.EngineRunner.exe. When empty, the app looks next to itself and then in the runner's build output.
    /// </summary>
    public string? EngineRunnerPath { get; set; }

    public string ReferenceDataBaseUrl { get; set; } = "https://raw.githubusercontent.com/QuantConnect/Lean/master/Data/";
}
