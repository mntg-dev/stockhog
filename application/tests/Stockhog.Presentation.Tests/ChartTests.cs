using Stockhog.Wpf.Controls.Charts;
using SkiaSharp;

namespace Stockhog.Presentation.Tests;

/// <summary>
/// Renders charts off screen and checks pixels, for drawing rules that are easy to get backwards.
/// </summary>
public class ChartTests
{
    [Fact]
    public void Heatmap_draws_row_zero_at_the_bottom()
    {
        // One column, two rows: the low value in row 0, the high value in row 1.
        var data = new HeatmapData("x", "y", ["a"], ["low", "high"], new double?[,] { { 0 }, { 10 } });
        var plot = new ScottPlot.Plot();
        HeatmapChart.Draw(plot, data);
        plot.Layout.Frameless();
        plot.HideGrid();

        using var bitmap = SKBitmap.Decode(plot.GetImage(200, 200).GetImageBytes());
        var bottom = Brightness(bitmap.GetPixel(100, 175));
        var top = Brightness(bitmap.GetPixel(100, 25));

        // Viridis runs from dark purple (low) to yellow (high).
        Assert.True(top > bottom, $"Expected the high value at the top: top {top}, bottom {bottom}.");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Heatmap_without_scores_or_with_a_single_one_draws_and_renders(bool oneScore)
    {
        // While an optimization starts, every cell is empty; this used to throw "min must be a real number".
        var values = new double?[2, 3];
        if (oneScore)
            values[1, 2] = 0.42;
        var plot = new ScottPlot.Plot();

        HeatmapChart.Draw(plot, new HeatmapData("x", "y", ["a", "b", "c"], ["d", "e"], values, oneScore ? (2, 1) : null));
        var image = plot.GetImage(300, 200);

        Assert.Equal(300, image.Width);
    }

    [Fact]
    public void Redrawing_a_heatmap_keeps_a_single_colour_bar_and_none_without_scores()
    {
        var plot = new ScottPlot.Plot();
        var scored = new HeatmapData("x", "y", ["a"], ["b"], new double?[,] { { 1 } });

        HeatmapChart.Draw(plot, scored);
        plot.Clear();
        HeatmapChart.Draw(plot, scored);
        Assert.Single(plot.Axes.GetPanels().OfType<ScottPlot.Panels.ColorBar>());

        plot.Clear();
        HeatmapChart.Draw(plot, scored with { Values = new double?[1, 1] });
        Assert.Empty(plot.Axes.GetPanels().OfType<ScottPlot.Panels.ColorBar>());
    }

    private static int Brightness(SKColor color) => color.Red + color.Green + color.Blue;
}
