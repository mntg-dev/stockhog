using Stockhog.Application.Abstractions.Messaging;
using Stockhog.Application.Abstractions.Results;
using Stockhog.Application.Proposals;
using Stockhog.Application.Scanning;
using Stockhog.Application.Trading.Safety;
using Stockhog.Domain.Autopilot;
using Stockhog.Domain.Common;
using Stockhog.Domain.Scanning;
using FluentValidation;

namespace Stockhog.Application.Autopilot;

/// <summary>Switches the autopilot off or into shadow mode; paper and live are refused until they exist.</summary>
public sealed record SetAutopilotModeCommand(AutopilotMode Mode) : ICommand<AutopilotMode>;

/// <param name="MaxConsecutiveLosses">Pause a pair after this many losing round trips in a row.</param>
/// <param name="MaxCapitalPercent">Most of the equity the autopilot's positions may take together.</param>
public sealed record ConfigureAutopilotCommand(int MaxConsecutiveLosses, decimal MaxCapitalPercent) : ICommand<bool>;

/// <summary>Follows the passed pairs of a scan (intraday strategies left out); pairs already followed are skipped.</summary>
public sealed record AddToAutopilotCommand(string ScanId) : ICommand<AddedToAutopilot>;

public sealed record AddedToAutopilot(int Added, int AlreadyFollowed, int Excluded);

public sealed record RemoveAutopilotAssignmentCommand(int AssignmentId) : ICommand<bool>;

public sealed record SetAutopilotAssignmentEnabledCommand(int AssignmentId, bool Enabled) : ICommand<bool>;

/// <summary>Follows a paused pair again and resets its count of losses in a row.</summary>
public sealed record ResumeAutopilotAssignmentCommand(int AssignmentId) : ICommand<bool>;

internal sealed class AddToAutopilotCommandValidator : AbstractValidator<AddToAutopilotCommand>
{
    public AddToAutopilotCommandValidator() =>
        RuleFor(c => c.ScanId).Must(ScanRunId.IsValid).WithMessage("'{PropertyValue}' is not a valid scan id.");
}

internal sealed class ConfigureAutopilotCommandValidator : AbstractValidator<ConfigureAutopilotCommand>
{
    public ConfigureAutopilotCommandValidator()
    {
        RuleFor(c => c.MaxConsecutiveLosses).InclusiveBetween(1, 20).WithMessage("Pause after between 1 and 20 losses in a row.");
        RuleFor(c => c.MaxCapitalPercent).GreaterThan(0).LessThanOrEqualTo(100).WithMessage("The autopilot may use above 0% and at most 100% of equity.");
    }
}

internal sealed class SetAutopilotModeCommandHandler(IAutopilotRepository autopilots, Audit audit) : ICommandHandler<SetAutopilotModeCommand, AutopilotMode>
{
    public async Task<Result<AutopilotMode>> HandleAsync(SetAutopilotModeCommand command, CancellationToken cancellationToken)
    {
        if (command.Mode is AutopilotMode.Paper or AutopilotMode.Live)
            return AutopilotErrors.ModeNotAvailable(command.Mode);

        var autopilot = await autopilots.GetOrCreateAsync(cancellationToken);
        if (autopilot.Mode != command.Mode)
        {
            await audit.RecordAsync(AuditCategory.Autopilot, "Mode", $"Autopilot: {autopilot.Mode} -> {command.Mode}.");
            autopilot.SetMode(command.Mode);
        }

        // Saved by UnitOfWorkBehavior.
        return autopilot.Mode;
    }
}

internal sealed class ConfigureAutopilotCommandHandler(IAutopilotRepository autopilots) : ICommandHandler<ConfigureAutopilotCommand, bool>
{
    public async Task<Result<bool>> HandleAsync(ConfigureAutopilotCommand command, CancellationToken cancellationToken)
    {
        (await autopilots.GetOrCreateAsync(cancellationToken)).Configure(command.MaxConsecutiveLosses, command.MaxCapitalPercent);
        return true;
    }
}

internal sealed class AddToAutopilotCommandHandler(IScanReader scans, IAutopilotRepository autopilots, TimeProvider clock)
    : ICommandHandler<AddToAutopilotCommand, AddedToAutopilot>
{
    public async Task<Result<AddedToAutopilot>> HandleAsync(AddToAutopilotCommand command, CancellationToken cancellationToken)
    {
        if (await scans.FindAsync(ScanRunId.From(command.ScanId), cancellationToken) is not { } scan)
            return ScanErrors.NotFound(command.ScanId);

        var autopilot = await autopilots.GetOrCreateAsync(cancellationToken);
        var passed = scan.Candidates.Where(c => c.Passed).OrderByDescending(c => c.Evaluation!.Score).ToList();
        var excluded = passed.Count(c => ProposalRules.ExcludedStrategies.Contains(c.Strategy.Value));

        var added = 0;
        var already = 0;
        try
        {
            foreach (var candidate in passed.Where(c => !ProposalRules.ExcludedStrategies.Contains(c.Strategy.Value)))
            {
                if (autopilot.Add(candidate.Symbol, candidate.Strategy, candidate.Evaluation!.Score, clock.GetUtcNow()) is null)
                    already++;
                else
                    added++;
            }
        }
        catch (DomainException ex)
        {
            return AutopilotErrors.Invalid(ex.Message);
        }

        // Saved by UnitOfWorkBehavior.
        return new AddedToAutopilot(added, already, excluded);
    }
}

internal sealed class RemoveAutopilotAssignmentCommandHandler(IAutopilotRepository autopilots) : ICommandHandler<RemoveAutopilotAssignmentCommand, bool>
{
    public async Task<Result<bool>> HandleAsync(RemoveAutopilotAssignmentCommand command, CancellationToken cancellationToken) =>
        await Change(autopilots, a => a.Remove(command.AssignmentId), cancellationToken);

    internal static async Task<Result<bool>> Change(IAutopilotRepository autopilots, Action<Domain.Autopilot.Autopilot> change, CancellationToken cancellationToken)
    {
        try
        {
            change(await autopilots.GetOrCreateAsync(cancellationToken));
            return true;
        }
        catch (DomainException ex)
        {
            return AutopilotErrors.Invalid(ex.Message);
        }
    }
}

internal sealed class SetAutopilotAssignmentEnabledCommandHandler(IAutopilotRepository autopilots) : ICommandHandler<SetAutopilotAssignmentEnabledCommand, bool>
{
    public Task<Result<bool>> HandleAsync(SetAutopilotAssignmentEnabledCommand command, CancellationToken cancellationToken) =>
        RemoveAutopilotAssignmentCommandHandler.Change(autopilots, a => a.SetEnabled(command.AssignmentId, command.Enabled), cancellationToken);
}

internal sealed class ResumeAutopilotAssignmentCommandHandler(IAutopilotRepository autopilots) : ICommandHandler<ResumeAutopilotAssignmentCommand, bool>
{
    public Task<Result<bool>> HandleAsync(ResumeAutopilotAssignmentCommand command, CancellationToken cancellationToken) =>
        RemoveAutopilotAssignmentCommandHandler.Change(autopilots, a => a.Resume(command.AssignmentId), cancellationToken);
}
