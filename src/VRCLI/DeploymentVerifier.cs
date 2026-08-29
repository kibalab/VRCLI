namespace KibaLab.WorldDeployment;

public sealed record DeploymentVerification(bool Success, string Message, RemoteContentSnapshot? Content = null);

public sealed class DeploymentVerifier
{
    public async Task<DeploymentVerification> VerifyAsync(
        VrchatApiClient api,
        DeploymentResult result,
        string expectedAuthorId,
        BuildPlatform platform,
        Action<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(result.Blueprint))
            return new DeploymentVerification(false, "The SDK result did not contain a Blueprint to verify.");

        string expectedPlatform = platform == BuildPlatform.Android ? "android" : "standalonewindows";
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        RemoteContentSnapshot? latest = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            latest = await api.GetContentAsync(result.Blueprint, progress, cancellationToken);
            if (!string.Equals(latest.AuthorId, expectedAuthorId, StringComparison.Ordinal))
                return new DeploymentVerification(false, "The uploaded content is not owned by the authenticated account.", latest);

            bool versionReady = !result.ServerVersion.HasValue || result.ServerVersion.Value <= 0 ||
                                latest.Version >= result.ServerVersion.Value;
            bool platformReady = latest.Packages.Any(package =>
                string.Equals(package.Platform, expectedPlatform, StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(package.AssetUrl));
            if (versionReady && platformReady)
            {
                return new DeploymentVerification(
                    true,
                    $"VRChat server confirmed {result.Blueprint} version {latest.Version} for {platform}.",
                    latest);
            }

            progress?.Invoke("The content record exists but its platform package is still processing; checking again.");
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }

        string observed = latest == null ? "no content record" : $"version {latest.Version} without a ready {platform} package";
        return new DeploymentVerification(false, "VRChat did not confirm the uploaded platform package within 30 seconds (" + observed + ").", latest);
    }
}
