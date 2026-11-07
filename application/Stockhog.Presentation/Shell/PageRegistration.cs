using Stockhog.Application.Common;
using Microsoft.Extensions.DependencyInjection;

namespace Stockhog.Presentation.Shell;

/// <summary>
/// A page as the menu shows it. Pages register themselves, so the shell does not know them in advance and a module
/// (e.g. the Pro edition) can add its own.
/// </summary>
/// <param name="Order">Position in the menu; sections appear in the order of their first page.</param>
/// <param name="Feature">The Pro feature the page belongs to, or null for the free core.</param>
/// <param name="PipelineKey">Label of the page's step in the strip "from the market to a trade", if it is one.</param>
/// <param name="PipelineArrow">False for a step that is an alternative to the one before, not the next one.</param>
public sealed record PageEntry(
    Type PageType,
    string TitleKey,
    string Glyph,
    string GroupKey,
    int Order,
    ProductFeature? Feature = null,
    string? PipelineKey = null,
    bool PipelineArrow = true);

public static class PageRegistration
{
    /// <summary>Registers the page view model as a singleton (its state survives navigation) and its menu entry.</summary>
    public static IServiceCollection AddPage<TPage>(
        this IServiceCollection services,
        string titleKey,
        string glyph,
        string groupKey,
        int order,
        ProductFeature? feature = null,
        string? pipelineKey = null,
        bool pipelineArrow = true)
        where TPage : PageViewModel
    {
        services.AddSingleton<TPage>();
        services.AddSingleton(new PageEntry(typeof(TPage), titleKey, glyph, groupKey, order, feature, pipelineKey, pipelineArrow));
        return services;
    }
}
