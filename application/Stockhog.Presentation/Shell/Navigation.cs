using CommunityToolkit.Mvvm.ComponentModel;
using Stockhog.Application.Backtesting;
using Stockhog.Presentation.Localization;
using Microsoft.Extensions.Logging;

namespace Stockhog.Presentation.Shell;

/// <summary>
/// A page of the shell. Pages are singletons: their state survives navigating away and back.
/// Derives from <see cref="ObservableValidator"/> so pages can validate their inputs (INotifyDataErrorInfo).
/// </summary>
public abstract partial class PageViewModel(StatusViewModel status, ILogger logger) : ObservableValidator
{
    private bool _loaded;

    // Progress<T> posts to the UI thread asynchronously, so a report can arrive after its operation ended.
    // Reports are applied only while their operation is still the current one.
    private int _operationId;

    protected StatusViewModel Status { get; } = status;

    [ObservableProperty] private bool _isBusy;

    partial void OnIsBusyChanged(bool value) => OnBusyChanged();

    /// <summary>Pages refresh their commands' CanExecute here.</summary>
    protected virtual void OnBusyChanged()
    {
    }

    /// <summary>Called every time the page is shown; the first call also runs <see cref="LoadAsync"/>.</summary>
    public async Task OnNavigatedToAsync()
    {
        if (!_loaded)
        {
            _loaded = true;
            await LoadAsync();
        }
    }

    protected virtual Task LoadAsync() => Task.CompletedTask;

    /// <summary>
    /// Runs an interactive operation with busy state, status messages and error handling.
    /// </summary>
    /// <param name="operation">Receives the operation id (for <see cref="CreateProgress{T}"/>) and returns the final status.</param>
    protected async Task RunOperationAsync(string startMessage, Func<int, Task<string>> operation)
    {
        var operationId = ++_operationId;
        IsBusy = Status.IsBusy = true;
        Status.Progress = 0;
        Status.Show(startMessage);

        try
        {
            Status.Show(await operation(operationId));
            Status.Progress = 100;
        }
        catch (OperationCanceledException)
        {
            Status.Show(Loc.Get("Status_Cancelled"));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Operation failed: {Operation}", startMessage);
            Status.Show(Loc.Format("Status_Failed", ex.Message));
        }
        finally
        {
            IsBusy = Status.IsBusy = false;
        }
    }

    protected Progress<T> CreateProgress<T>(int operationId, Action<T> apply) =>
        new(value =>
        {
            if (IsBusy && operationId == _operationId)
                apply(value);
        });
}

/// <param name="TitleKey">Resource key of the menu label.</param>
/// <param name="Glyph">Segoe Fluent Icons / MDL2 Assets code point.</param>
public sealed record NavigationItem(string TitleKey, string Glyph, PageViewModel Page);

// Messages between pages (CommunityToolkit messenger): pages stay unaware of each other.

/// <summary>Backtest runs were added or changed state.</summary>
public sealed record BacktestRunsChangedMessage;

/// <summary>Locally stored market data changed.</summary>
public sealed record MarketDataChangedMessage;

/// <summary>The stored instrument list was refreshed.</summary>
public sealed record InstrumentsChangedMessage;

/// <summary>Select a strategy, with its default parameters, on the backtest page.</summary>
public sealed record SelectBacktestStrategyMessage(string StrategyName);

/// <summary>Select a strategy on the optimization page.</summary>
public sealed record OptimizeStrategyMessage(string StrategyName);

/// <summary>Start a question to the assistant, e.g. about runs selected on the compare page.</summary>
public sealed record AskAssistantMessage(string Question);

/// <summary>The assistant proposed an order: fill in the order ticket for the user to review. Nothing is sent.</summary>
public sealed record PrefillOrderTicketMessage(Application.Assistant.OrderProposal Proposal);

/// <summary>Show the report of a finished run on the backtest page.</summary>
public sealed record OpenBacktestRunMessage(BacktestRunSummary Run);

/// <summary>Switch the shell to the page of the given type.</summary>
public sealed record NavigateMessage(Type PageType);

/// <summary>An optimization was started, finished a trial, or ended.</summary>
public sealed record OptimizationsChangedMessage;

/// <summary>Analyze a finished run on the analysis page.</summary>
public sealed record OpenAnalysisMessage(BacktestRunSummary Run);

/// <summary>Fill the backtest form, e.g. with the best parameters of an optimization.</summary>
public sealed record ApplyBacktestSetupMessage(
    string StrategyName,
    string Symbol,
    DateOnly Start,
    DateOnly End,
    decimal InitialCapital,
    IReadOnlyDictionary<string, string> Parameters);
