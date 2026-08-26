using System.Text.Json;
using System.Text.Json.Serialization;

namespace KibaLab.VRCLI;

public sealed class VrcliProjectConfig
{
    public string? Project { get; init; }
    public string? Blueprint { get; init; }

    [JsonPropertyName("new")]
    public bool? NewWorld { get; init; }

    public string? Scene { get; init; }
    public string? Platform { get; init; }
    public string? Login { get; init; }
    public string? Title { get; init; }
    public string? Description { get; init; }
    public string? Thumbnail { get; init; }
    public int? Capacity { get; init; }
    public int? RecommendedCapacity { get; init; }
    public string[]? Tags { get; init; }
    public string? BlueprintOutput { get; init; }
    public string? Unity { get; init; }
    public int? Timeout { get; init; }
    public bool? Plain { get; init; }
    public bool? Yes { get; init; }
    public bool? SkipVpmResolve { get; init; }

    public static VrcliProjectConfig Load(string path)
    {
        string json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<VrcliProjectConfig>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
        }) ?? throw new JsonException("The configuration file is empty.");
    }
}
