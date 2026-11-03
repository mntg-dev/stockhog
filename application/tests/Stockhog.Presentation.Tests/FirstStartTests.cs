using System.Globalization;
using Stockhog.Presentation.Localization;
using Stockhog.Presentation.Settings;

namespace Stockhog.Presentation.Tests;

public class FirstStartTests
{
    [Theory]
    [InlineData("pl-PL", "pl-PL")]
    [InlineData("en-GB", "en-US")]
    [InlineData("de-DE", "en-US")]
    public void A_new_user_gets_the_windows_language_when_it_is_supported_and_english_otherwise(string windows, string expected) =>
        Assert.Equal(expected, Loc.Choose("", CultureInfo.GetCultureInfo(windows)).Name);

    [Fact]
    public void A_saved_language_wins_over_windows() =>
        Assert.Equal("pl-PL", Loc.Choose("pl-PL", CultureInfo.GetCultureInfo("en-US")).Name);

    [Fact]
    public void The_risk_notice_is_asked_once_per_version()
    {
        var fresh = new UserSettings();
        var accepted = TermsOfUse.Accept(fresh);

        Assert.True(TermsOfUse.NeedsAcceptance(fresh));
        Assert.False(TermsOfUse.NeedsAcceptance(accepted));
        Assert.True(TermsOfUse.NeedsAcceptance(accepted with { AcceptedTermsVersion = TermsOfUse.CurrentVersion - 1 }));
    }
}
