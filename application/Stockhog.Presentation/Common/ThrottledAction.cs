namespace Stockhog.Presentation.Common;

/// <summary>
/// Collapses bursts of requests from any thread into at most one run of <c>action</c> per interval,
/// executed on the UI thread. Used for job progress and log lines, which can arrive hundreds of times per second.
/// </summary>
public sealed class ThrottledAction(TimeSpan interval, Action action, SynchronizationContext? uiContext)
{
    private int _scheduled;

    /// <summary>Thread-safe. Returns at once; the action runs later on the UI thread.</summary>
    public void Request()
    {
        if (Interlocked.Exchange(ref _scheduled, 1) == 1)
            return;

        _ = RunAfterDelayAsync();
    }

    private async Task RunAfterDelayAsync()
    {
        await Task.Delay(interval).ConfigureAwait(false);

        // Reset before running: a request arriving during the run schedules another one.
        Volatile.Write(ref _scheduled, 0);

        if (uiContext is null)
            action();
        else
            uiContext.Post(_ => action(), null);
    }
}
