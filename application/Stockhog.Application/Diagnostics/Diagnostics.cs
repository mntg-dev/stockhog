using Stockhog.Application.Abstractions.Messaging;
using Stockhog.Application.Abstractions.Results;
using Stockhog.Application.Trading.Safety;
using FluentValidation;

namespace Stockhog.Application.Diagnostics;

/// <summary>Writes the support bundle; implemented by infrastructure, which knows the files.</summary>
public interface IDiagnosticsExporter
{
    /// <param name="facts">About this installation, e.g. the version; written as they are.</param>
    /// <param name="auditLines">The latest audit entries, newest first.</param>
    Task<DiagnosticsExport> ExportAsync(string targetPath, IReadOnlyDictionary<string, string> facts, IReadOnlyList<string> auditLines,
        CancellationToken cancellationToken = default);
}

public sealed record DiagnosticsExport(string Path, int Files, long Bytes);

/// <summary>
/// Packs what support needs into one zip: recent logs, settings, risk limits, the latest audit entries and facts
/// about the installation. Keys are never included and anything that looks like one is masked.
/// </summary>
public sealed record ExportDiagnosticsCommand(string TargetPath, IReadOnlyDictionary<string, string> Facts) : ICommand<DiagnosticsExport>
{
    public const int AuditEntries = 500;
}

internal sealed class ExportDiagnosticsCommandValidator : AbstractValidator<ExportDiagnosticsCommand>
{
    public ExportDiagnosticsCommandValidator()
    {
        RuleFor(c => c.TargetPath)
            .NotEmpty()
            .Must(p => Path.IsPathFullyQualified(p) && p.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            .WithMessage("Choose a .zip file to save the diagnostics to.");
        RuleFor(c => c.Facts).NotNull();
    }
}

internal sealed class ExportDiagnosticsCommandHandler(IDiagnosticsExporter exporter, IAuditLog audit) : ICommandHandler<ExportDiagnosticsCommand, DiagnosticsExport>
{
    public async Task<Result<DiagnosticsExport>> HandleAsync(ExportDiagnosticsCommand command, CancellationToken cancellationToken)
    {
        var entries = await audit.ListAsync(ExportDiagnosticsCommand.AuditEntries, cancellationToken);
        var lines = entries
            .Select(e => $"{e.Time:O}\t{e.Mode}\t{e.Category}\t{e.Action}\t{e.Symbol}\t{e.ClientOrderId}\t{e.Details}")
            .ToList();

        try
        {
            return await exporter.ExportAsync(command.TargetPath, command.Facts, lines, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Error.Failure("Diagnostics.WriteFailed", $"The diagnostics could not be written: {ex.Message}");
        }
    }
}
