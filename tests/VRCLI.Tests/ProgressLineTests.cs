using KibaLab.WorldDeployment;

namespace WorldDeployment.Tests;

public sealed class ProgressLineTests
{
    [Fact]
    public void ParsesPhaseMarkerAndUploadProgress()
    {
        bool parsed = ProgressLine.TryParse(
            "[VRCLI][00:00:12.345][UPLOAD] ▶ bundle: Uploading 40.0% (overall 20.0%)",
            out ProgressLine? progress);

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
        bool parsed = ProgressLine.TryParse(
            "Unity: [VRCLI][00:00:01.000][AUTH] Authentication succeeded.",
            out ProgressLine? progress);

        Assert.True(parsed);
        Assert.Equal("AUTH", progress?.Area);
    }

    [Fact]
    public void IgnoresOrdinaryUnityOutput()
    {
        Assert.False(ProgressLine.TryParse("AssetDatabase refresh completed", out _));
    }
}
