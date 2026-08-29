using System.Text.Json;

namespace KibaLab.WorldDeployment;

public static class RecoveryManifestFile
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        IncludeFields = true,
        PropertyNameCaseInsensitive = true
    };

    public static RecoveryManifest Load(string path)
    {
        using FileStream stream = File.OpenRead(path);
        RecoveryManifest manifest = JsonSerializer.Deserialize<RecoveryManifest>(stream, JsonOptions)
                                    ?? throw new JsonException("The recovery manifest is empty.");
        Validate(manifest, path);
        return manifest;
    }

    private static void Validate(RecoveryManifest manifest, string path)
    {
        if (manifest.FormatVersion != 1)
            throw new JsonException($"Unsupported recovery manifest format {manifest.FormatVersion} in {path}.");
        if (string.IsNullOrWhiteSpace(manifest.ProjectPath) || !Directory.Exists(manifest.ProjectPath))
            throw new JsonException("The recovery manifest project directory does not exist.");
        if (string.IsNullOrWhiteSpace(manifest.BundlePath) || !File.Exists(manifest.BundlePath))
            throw new JsonException("The recovery bundle does not exist.");
        if (manifest.ContentType is not ("World" or "Avatar"))
            throw new JsonException("The recovery content type must be World or Avatar.");
        if (manifest.ContentType == "World" && string.IsNullOrWhiteSpace(manifest.Signature))
            throw new JsonException("A world recovery manifest must contain its bundle signature.");
        if (manifest.Platform is not ("StandaloneWindows64" or "Android"))
            throw new JsonException("The recovery platform must be StandaloneWindows64 or Android.");
    }
}
