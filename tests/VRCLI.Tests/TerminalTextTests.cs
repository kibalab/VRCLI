using KibaLab.WorldDeployment;

namespace WorldDeployment.Tests;

public sealed class TerminalTextTests
{
    [Theory]
    [InlineData("VRCLI", 5)]
    [InlineData("한글", 4)]
    [InlineData("日本", 4)]
    [InlineData("◆", 1)]
    [InlineData("🌐", 2)]
    public void MeasuresTerminalCellWidth(string value, int expected)
    {
        Assert.Equal(expected, TerminalText.Width(value));
    }

    [Fact]
    public void TruncatesWithoutSplittingWideCharacters()
    {
        string result = TerminalText.Truncate("한글 world", 6);

        Assert.Equal("한글 …", result);
        Assert.Equal(6, TerminalText.Width(result));
    }
}
