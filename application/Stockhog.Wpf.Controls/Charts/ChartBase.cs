using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using ScottPlot;
using ScottPlot.WPF;

namespace Stockhog.Wpf.Controls.Charts;

/// <summary>
/// Hosts ScottPlot plots and redraws them whenever a dependency property declared with
/// <see cref="RedrawOnChange"/> changes. Derived charts only describe what to draw.
/// </summary>
public abstract class ChartBase : ContentControl
{
    protected static readonly ScottPlot.Color Positive = ScottPlot.Color.FromHex("#2E7D32");
    protected static readonly ScottPlot.Color Negative = ScottPlot.Color.FromHex("#C62828");
    protected static readonly ScottPlot.Color Accent = ScottPlot.Color.FromHex("#1565C0");

    protected ChartBase()
    {
        Loaded += (_, _) => Redraw();
    }

    protected static PropertyMetadata RedrawOnChange(object? defaultValue = null) =>
        new(defaultValue, (d, _) => ((ChartBase)d).Redraw());

    protected static WpfPlot CreatePlot()
    {
        var plot = new WpfPlot();
        plot.Plot.FigureBackground.Color = ScottPlot.Colors.White;
        plot.Plot.Grid.MajorLineColor = ScottPlot.Color.FromHex("#EEEEEE");
        return plot;
    }

    /// <summary>Clears and draws every plot; called on the UI thread.</summary>
    protected abstract void Render();

    /// <summary>
    /// A chart must never break the page it is on: a drawing error (e.g. unexpected data) is traced and the chart is
    /// left empty instead of throwing into the view model that set the data, or into WPF's render loop.
    /// </summary>
    private void Redraw()
    {
        if (!IsLoaded)
            return;

        try
        {
            Render();
        }
        catch (Exception ex)
        {
            Trace.TraceError($"{GetType().Name} could not draw its data: {ex}");
            foreach (var plot in Plots(Content))
            {
                plot.Plot.Clear();
                plot.Refresh();
            }
        }
    }

    private static IEnumerable<WpfPlot> Plots(object? element) => element switch
    {
        WpfPlot plot => [plot],
        Panel panel => panel.Children.Cast<object>().SelectMany(Plots),
        ContentControl control => Plots(control.Content),
        Decorator decorator => Plots(decorator.Child),
        _ => []
    };

    /// <summary>Without data, a date axis would start at 1899-12-30 (OLE date zero): show the last month instead.</summary>
    protected static void ShowRecentDates(Plot plot) =>
        plot.Axes.SetLimitsX(DateTime.Today.AddDays(-30).ToOADate(), DateTime.Today.AddDays(1).ToOADate());

    protected static double[] ToOADates(IEnumerable<DateTime> times) => times.Select(t => t.ToOADate()).ToArray();

    protected static void PercentTicks(IYAxis axis) =>
        axis.TickGenerator = new ScottPlot.TickGenerators.NumericAutomatic { LabelFormatter = value => $"{value:0.#}%" };
}
