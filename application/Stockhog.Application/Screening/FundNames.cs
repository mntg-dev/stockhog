using System.Text.RegularExpressions;

namespace Stockhog.Application.Screening;

/// <summary>
/// Tells funds (ETFs, ETNs, leveraged and inverse products, trusts holding a commodity) from company shares by name.
/// The broker's asset list has no such flag, so this is a heuristic: it errs on the side of calling something a fund,
/// e.g. a closed-end "... Fund Inc." is treated as a fund too. "Trust" alone is not enough: many REITs are trusts.
/// </summary>
public static partial class FundNames
{
    public static bool LooksLikeFund(string name) => FundPattern().IsMatch(name);

    [GeneratedRegex(
        @"\b(ETF|ETN|ETP|ETFs|Fund|Funds|iShares|SPDR|ProShares|Direxion|GraniteShares|Leveraged|Inverse|UltraPro|Ultra ?Short|Index)\b" +
        @"|\b\d+(\.\d+)?[xX]\b" +
        @"|\bTrust,? Series\b" +
        @"|\bDaily\b.*\b(Bull|Bear|Long|Short)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FundPattern();
}
