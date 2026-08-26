using KibaLab.VRCLI;

namespace VRCLI.Tests;

public sealed class VrcliProgressLineTests
{
    [Fact]
    public void ParsesPhaseMarkerAndUploadProgress()
    {
        bool parsed = VrcliProgressLine.TryParse(
            "[VRCLI][00:00:12.345][UPLOAD] ▶ bundle: Uploading 40.0% (overall 20.0%)",
            out VrcliProgressLine? progress);

        Assert.True(parsed);
        Assert.NotNull(progress);
        Assert.Equal("UPLOAD", progress.Area);
        Assert.True(progress.StartsPhase);
        Assert.Equal(0.2d, progress.OverallProgress);
        Assert.DoesNotContain("▶", progress.Message);
    }

    [Fact]
    public void FindsVrcliRecordAfterUnityPrefix()
    {
        bool parsed = VrcliProgressLine.TryParse(
            "Unity: [VRCLI][00:00:01.000][AUTH] Authentication succeeded.",
            out VrcliProgressLine? progress);

        Assert.True(parsed);
        Assert.Equal("AUTH", progress?.Area);
    }

    [Fact]
    public void IgnoresOrdinaryUnityOutput()
    {
        Assert.False(VrcliProgressLine.TryParse("AssetDatabase refresh completed", out _));
    }
}
