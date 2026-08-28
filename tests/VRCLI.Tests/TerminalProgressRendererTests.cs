using System.Text.RegularExpressions;
using KibaLab.WorldDeployment;

namespace WorldDeployment.Tests;

public sealed class TerminalProgressRendererTests
{
    [Fact]
    public async Task DoesNotRedrawTheTitleForEverySpinnerFrame()
    {
        StringWriter output = new();
        TerminalProgressRenderer renderer = new(output, () => (100, 40));
        renderer.Start();
        await Task.Delay(350);
        string activeOutput = output.ToString();
        await renderer.FinishAsync(true);

        Assert.Equal(1, CountOccurrences(activeOutput, "\x1b[1;1H"));
    }

    [Fact]
    public async Task MetadataProgressHasNoUnityOrVpmStages()
    {
        StringWriter output = new();
        TerminalProgressRenderer renderer = new(output, OperationMode.Meta, () => (100, 40));
        renderer.Start();
        string screen = CaptureScreen(output.ToString());
        await renderer.FinishAsync(true);

        Assert.Contains("WORLD META", screen);
        Assert.DoesNotContain("Unity startup", screen);
        Assert.DoesNotContain("VPM dependencies", screen);
    }

    [Fact]
    public async Task RendersCheckSpecificWorkflow()
    {
        StringWriter output = new();
        TerminalProgressRenderer renderer = new(output, OperationMode.Check, () => (100, 40));
        renderer.Start();
        renderer.Report("CHECK", "No blocking SDK issues", true);
        string screen = CaptureScreen(output.ToString());
        await renderer.FinishAsync(true);

        Assert.Contains("PREFLIGHT CHECK", screen);
        Assert.Contains("Preflight report", screen);
        Assert.DoesNotContain("Bundle signature", screen);
        Assert.Contains("Preflight check passed", output.ToString());
    }

    [Fact]
    public async Task RendersStagesContextAndProgressBar()
    {
        StringWriter output = new();
        TerminalProgressRenderer renderer = new(output, () => (100, 40));
        renderer.SetOverview(
            "Example Project",
            "wrld_example",
            "Assets/Scenes/Main.unity",
            "StandaloneWindows64");
        renderer.Start();

        await renderer.OnLineAsync(
            "[VRCLI][00:00:00.050][UNITY] ▶ Launching Unity and compiling project scripts.",
            false,
            CancellationToken.None);
        await renderer.OnLineAsync(
            "[VRCLI][00:00:00.100][AUTH] ▶ Initializing authentication.",
            false,
            CancellationToken.None);
        await renderer.OnLineAsync(
            "[VRCLI][00:00:01.000][CONTEXT] Project: Example World",
            false,
            CancellationToken.None);
        await renderer.OnLineAsync(
            "[VRCLI][00:00:02.000][UPLOAD] ▶ Starting upload.",
            false,
            CancellationToken.None);
        await renderer.OnLineAsync(
            "[VRCLI][00:00:03.000][UPLOAD] bundle: Uploading 50.0% (overall 50.0%)",
            false,
            CancellationToken.None);
        string activeScreen = CaptureScreen(output.ToString());
        await renderer.FinishAsync(true);

        string rendered = output.ToString();
        Assert.Contains(" ___ ___  ______  ______  _____    _______ ", activeScreen);
        Assert.Contains("VRCLI", activeScreen);
        Assert.Contains("WORLD DEPLOY", activeScreen);
        Assert.Contains("Example Project", activeScreen);
        Assert.Contains("Assets/Scenes/Main.unity", activeScreen);
        Assert.Contains("Project context", rendered);
        Assert.Contains("Unity startup", rendered);
        Assert.Contains("Authentication", rendered);
        Assert.Contains("50%", activeScreen);
        Assert.Contains("\x1b[?1049h", rendered);
        Assert.Contains("\x1b[?1049l", rendered);
        Assert.Contains("\x1b[2K", rendered);
        Assert.DoesNotMatch("\\x1b\\[\\d+F", rendered);
        Assert.Equal(1, CountOccurrences(rendered, "\x1b[2J"));
        Assert.Contains("Deployment complete", rendered);
    }

    [Fact]
    public async Task ShowsAtMostFourResponsiveDetailLinesForTallTerminal()
    {
        StringWriter output = new();
        TerminalProgressRenderer renderer = new(output, () => (100, 40));
        renderer.Start();
        renderer.Report("AUTH", "Initializing authentication", true);
        renderer.Report("AUTH", "Remote configuration ready");
        renderer.Report("AUTH", "Checking saved session");
        renderer.Report("AUTH", "Starting credential login");
        renderer.Report("AUTH", "Primary credentials accepted");
        renderer.Report("AUTH", "Submitting verification");
        string screen = CaptureScreen(output.ToString());
        await renderer.FinishAsync(true);

        Assert.Equal(4, CountOccurrences(screen, "│ "));
        Assert.DoesNotContain("Initializing authentication", screen);
        Assert.Contains("Primary credentials accepted", screen);
    }

    [Fact]
    public async Task HidesDetailLinesForShortTerminal()
    {
        StringWriter output = new();
        TerminalProgressRenderer renderer = new(output, () => (72, 12));
        renderer.Start();
        renderer.Report("AUTH", "Initializing authentication", true);
        renderer.Report("AUTH", "Remote configuration ready");
        renderer.Report("AUTH", "Checking saved session");
        string screen = CaptureScreen(output.ToString());
        await renderer.FinishAsync(false);

        Assert.DoesNotContain("    │ ", screen);
    }

    [Theory]
    [InlineData(18, 1)]
    [InlineData(24, 2)]
    [InlineData(30, 3)]
    [InlineData(40, 4)]
    public async Task DetailCapacityRespondsToTerminalHeight(int height, int expected)
    {
        StringWriter output = new();
        TerminalProgressRenderer renderer = new(output, () => (80, height));
        renderer.Start();
        renderer.Report("AUTH", "Authentication started", true);
        for (int index = 0; index < 8; index++) renderer.Report("AUTH", "Detail " + index);
        string screen = CaptureScreen(output.ToString());
        await renderer.FinishAsync(true);

        Assert.Equal(expected, CountOccurrences(screen, "│ "));
    }

    private static int CountOccurrences(string value, string pattern)
    {
        int count = 0;
        int index = 0;
        while ((index = value.IndexOf(pattern, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += pattern.Length;
        }
        return count;
    }

    [Fact]
    public void PausesActiveLineForInteractiveVerification()
    {
        StringWriter output = new();
        TerminalProgressRenderer renderer = new(output, () => (100, 40));
        renderer.Start();
        renderer.Report("AUTH", "Waiting for a challenge", true);

        string answer = renderer.PromptForTwoFactor(() => "accepted");

        Assert.Equal("accepted", answer);
        Assert.Contains("Verification required", output.ToString());
        Assert.Contains("Submitting two-factor verification", output.ToString());
    }

    private static string CaptureScreen(string output)
    {
        Dictionary<int, string> rows = new();
        MatchCollection updates = Regex.Matches(
            output,
            "\\x1b\\[(\\d+);1H\\x1b\\[2K(.*?)(?=\\x1b\\[\\d+;1H|\\x1b\\[\\?25h|\\x1b\\[\\?1049l|$)",
            RegexOptions.Singleline);
        foreach (Match update in updates)
        {
            int row = int.Parse(update.Groups[1].Value);
            rows[row] = Regex.Replace(update.Groups[2].Value, "\\x1b\\[[0-9;]*m", string.Empty);
        }
        return string.Join('\n', rows.OrderBy(row => row.Key).Select(row => row.Value));
    }
}
