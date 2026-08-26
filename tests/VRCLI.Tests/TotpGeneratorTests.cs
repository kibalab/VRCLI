using KibaLab.WorldDeployment.Editor;

namespace WorldDeployment.Tests;

public sealed class TotpGeneratorTests
{
    [Theory]
    [InlineData(59, "94287082")]
    [InlineData(1111111109, "07081804")]
    [InlineData(1111111111, "14050471")]
    [InlineData(1234567890, "89005924")]
    [InlineData(2000000000, "69279037")]
    [InlineData(20000000000, "65353130")]
    public void MatchesRfc6238Sha1Vectors(long unixTime, string expected)
    {
        const string secret = "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ";

        string code = TotpGenerator.GenerateCode(
            secret,
            DateTimeOffset.FromUnixTimeSeconds(unixTime),
            8);

        Assert.Equal(expected, code);
    }

    [Fact]
    public void AcceptsFormattedBase32Secret()
    {
        DateTimeOffset timestamp = DateTimeOffset.FromUnixTimeSeconds(59);

        string code = TotpGenerator.GenerateCode(
            "gezd-gnbv gy3tqojq gezdgnbvgy3tqojq====",
            timestamp,
            8);

        Assert.Equal("94287082", code);
    }

    [Fact]
    public void RejectsInvalidBase32Secret()
    {
        Assert.Throws<ArgumentException>(() =>
            TotpGenerator.GenerateCode("NOT_VALID_!", DateTimeOffset.UnixEpoch));
    }
}
