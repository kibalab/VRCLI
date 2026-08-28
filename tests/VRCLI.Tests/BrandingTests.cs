using KibaLab.WorldDeployment;

namespace WorldDeployment.Tests;

public sealed class BrandingTests
{
    [Fact]
    public void KeepsAsciiLogoAligned()
    {
        Assert.Equal(4, Branding.LogoLines.Count);
        Assert.All(Branding.LogoLines, line => Assert.Equal(43, line.Length));
        Assert.Contains("|   __ \\|", Branding.LogoText);
        Assert.Equal(43, Branding.FooterLine.Length);
        Assert.Matches(@"^v\d+\.\d+\.\d+\s+by KIBA_$", Branding.FooterLine);
        Assert.EndsWith(Environment.NewLine + Branding.FooterLine, Branding.LogoText);
    }

    [Fact]
    public void FallsBackToWordmarkWhenTerminalIsNarrow()
    {
        Assert.Equal(["VRCLI"], Branding.Fit(42));
        Assert.Equal(Branding.LogoLines, Branding.Fit(43));
    }

    [Fact]
    public void HelpStartsWithLogoAndDocumentsParameterLogin()
    {
        Assert.StartsWith(Branding.LogoText, DeploymentApplication.HelpText);
        Assert.Contains("--login <username-or-email>", DeploymentApplication.HelpText);
        Assert.Contains("--password <password>", DeploymentApplication.HelpText);
    }
}
