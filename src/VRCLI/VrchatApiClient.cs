using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Blake2Fast;

namespace KibaLab.WorldDeployment;

public sealed record VrchatUser(string Id, string DisplayName);

public sealed record VrchatSessionTokens(string AuthToken, string? TwoFactorToken);

public sealed record VrchatLoginChallenge(
    VrchatUser? User,
    IReadOnlyList<string> RequiredTwoFactorMethods)
{
    public bool RequiresTwoFactor => RequiredTwoFactorMethods.Count > 0;
}

public sealed record WorldMetadataSnapshot(
    string Id,
    string AuthorId,
    string Title,
    string Description,
    int Capacity,
    int RecommendedCapacity,
    IReadOnlyList<string> Tags,
    string? ImageUrl,
    int Version);

public sealed record AvatarMetadataSnapshot(
    string Id,
    string AuthorId,
    string Title,
    string Description,
    IReadOnlyList<string> Tags,
    string? ImageUrl,
    int Version);

public sealed record MetadataChange(string Field, string Before, string After);

public sealed record RemotePlatformPackage(string Platform, string? AssetUrl, int? AssetVersion);

public sealed record RemoteContentSnapshot(
    string Id,
    string AuthorId,
    int Version,
    IReadOnlyList<RemotePlatformPackage> Packages);

public class VrchatApiException(string message, int? statusCode = null) : Exception(message)
{
    public int? StatusCode { get; } = statusCode;
}

public sealed class VrchatCredentialException(string message) : Exception(message);

public sealed class VrchatApiClient : IDisposable
{
    private static readonly Uri ApiRoot = new("https://api.vrchat.cloud/api/1/");
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient client;
    private readonly bool ownsClient;
    private readonly CookieContainer? cookies;
    private readonly Func<TimeSpan, CancellationToken, Task> delayAsync;

    public VrchatUser? CurrentUser { get; private set; }

    public VrchatApiClient(
        HttpMessageHandler? messageHandler = null,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null)
    {
        this.delayAsync = delayAsync ?? Task.Delay;
        if (messageHandler == null)
        {
            cookies = new CookieContainer();
            messageHandler = new HttpClientHandler
            {
                CookieContainer = cookies,
                AutomaticDecompression = DecompressionMethods.All,
                UseCookies = true,
                UseProxy = false
            };
        }

        client = new HttpClient(messageHandler, disposeHandler: true)
        {
            BaseAddress = ApiRoot,
            Timeout = TimeSpan.FromMinutes(10)
        };
        ownsClient = true;
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "VRC.Core.BestHTTP");
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json");
    }

    public async Task<VrchatUser> ResumeSessionAsync(
        VrchatSessionTokens session,
        CancellationToken cancellationToken = default)
    {
        if (cookies == null)
            throw new InvalidOperationException("Session import is unavailable with a custom HTTP handler.");
        if (string.IsNullOrWhiteSpace(session.AuthToken))
            throw new VrchatApiException("The saved VRChat session does not contain an authentication token.");

        cookies.Add(ApiRoot, new Cookie("auth", session.AuthToken));
        if (!string.IsNullOrWhiteSpace(session.TwoFactorToken))
            cookies.Add(ApiRoot, new Cookie("twoFactorAuth", session.TwoFactorToken));
        return await GetCurrentUserAsync(cancellationToken);
    }

    public VrchatSessionTokens ExportSession()
    {
        if (cookies == null)
            throw new InvalidOperationException("Session export is unavailable with a custom HTTP handler.");
        CookieCollection established = cookies.GetCookies(ApiRoot);
        string? authToken = established["auth"]?.Value;
        if (string.IsNullOrWhiteSpace(authToken))
            throw new VrchatApiException("VRChat did not issue an authentication session.");
        return new VrchatSessionTokens(authToken, established["twoFactorAuth"]?.Value);
    }

    public async Task<VrchatLoginChallenge> BeginLoginAsync(
        string username,
        string password,
        CancellationToken cancellationToken = default)
    {
        byte[] credentials = Encoding.UTF8.GetBytes(username + ":" + password);
        try
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                "Basic",
                Convert.ToBase64String(credentials));
        }
        finally
        {
            Array.Clear(credentials, 0, credentials.Length);
        }

        using HttpResponseMessage response = await client.GetAsync("auth/user", cancellationToken);
        string body = await ReadSuccessfulBodyAsync(response, cancellationToken);
        using JsonDocument json = ParseJson(body, "authentication");
        string[] methods = ReadTwoFactorMethods(json.RootElement);
        VrchatUser? user = ReadUser(json.RootElement);

        if (methods.Length == 0 && user == null)
            throw new VrchatApiException("VRChat did not return a verified user for these credentials.");

        if (methods.Length > 0)
        {
            bool hasAuthenticationCookie = response.Headers.TryGetValues("Set-Cookie", out IEnumerable<string>? cookies) &&
                                           cookies.Any(cookie => cookie.StartsWith("auth=", StringComparison.OrdinalIgnoreCase));
            if (!hasAuthenticationCookie)
                throw new VrchatApiException(
                    "VRChat returned an incomplete two-factor challenge. Check the username/email and password, then try again.");
        }

        if (methods.Length == 0) client.DefaultRequestHeaders.Authorization = null;
        CurrentUser = user;
        return new VrchatLoginChallenge(user, methods);
    }

    public async Task<VrchatUser> CompleteLoginAsync(
        string method,
        string code,
        CancellationToken cancellationToken = default)
    {
        string endpoint = method.ToLowerInvariant() switch
        {
            "totp" => "auth/twofactorauth/totp/verify",
            "emailotp" => "auth/twofactorauth/emailotp/verify",
            "otp" => "auth/twofactorauth/otp/verify",
            _ => throw new VrchatApiException("VRChat requested an unsupported two-factor method: " + method)
        };

        client.DefaultRequestHeaders.Authorization = null;
        await SendJsonAsync(HttpMethod.Post, endpoint, new { code }, cancellationToken);
        CurrentUser = await GetCurrentUserAsync(cancellationToken);
        return CurrentUser;
    }

    public async Task<VrchatUser> GetCurrentUserAsync(CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await client.GetAsync("auth/user", cancellationToken);
        string body = await ReadSuccessfulBodyAsync(response, cancellationToken);
        using JsonDocument json = ParseJson(body, "authentication");
        VrchatUser? user = ReadUser(json.RootElement);
        if (user == null) throw new VrchatApiException("VRChat did not return a complete authenticated user.");
        CurrentUser = user;
        return user;
    }

    public async Task<WorldMetadataSnapshot> GetWorldAsync(
        string worldId,
        CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await client.GetAsync("worlds/" + worldId, cancellationToken);
        string body = await ReadSuccessfulBodyAsync(response, cancellationToken);
        return DeserializeWorld(body);
    }

    public async Task<RemoteContentSnapshot> GetContentAsync(
        string blueprint,
        Action<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        string endpoint = blueprint.StartsWith("wrld_", StringComparison.Ordinal)
            ? "worlds/" + blueprint
            : blueprint.StartsWith("avtr_", StringComparison.Ordinal)
                ? "avatars/" + blueprint
                : throw new ArgumentException("Blueprint must begin with wrld_ or avtr_.", nameof(blueprint));

        string body = await GetWithRetryAsync(endpoint, progress, cancellationToken);
        using JsonDocument document = ParseJson(body, "content verification");
        JsonElement root = document.RootElement;
        string? id = ReadString(root, "id");
        string? authorId = ReadString(root, "authorId");
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(authorId))
            throw new VrchatApiException("VRChat returned an incomplete content record.");

        int version = root.TryGetProperty("version", out JsonElement versionElement) &&
                      versionElement.TryGetInt32(out int parsedVersion)
            ? parsedVersion
            : 0;
        List<RemotePlatformPackage> packages = [];
        if (root.TryGetProperty("unityPackages", out JsonElement packageArray) &&
            packageArray.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement package in packageArray.EnumerateArray())
            {
                string? platform = ReadString(package, "platform");
                if (string.IsNullOrWhiteSpace(platform)) continue;
                int? assetVersion = package.TryGetProperty("assetVersion", out JsonElement assetVersionElement) &&
                                    assetVersionElement.TryGetInt32(out int parsedAssetVersion)
                    ? parsedAssetVersion
                    : null;
                packages.Add(new RemotePlatformPackage(
                    platform,
                    ReadString(package, "assetUrl"),
                    assetVersion));
            }
        }

        return new RemoteContentSnapshot(id, authorId, version, packages);
    }

    public async Task<WorldMetadataSnapshot> UpdateWorldAsync(
        WorldMetadataSnapshot current,
        WorldMetadataSnapshot desired,
        string? thumbnailPath,
        Action<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        EnsureOwner(current);
        Dictionary<string, object> changes = new();
        if (!string.Equals(current.Title, desired.Title, StringComparison.Ordinal)) changes["name"] = desired.Title;
        if (!string.Equals(current.Description, desired.Description, StringComparison.Ordinal)) changes["description"] = desired.Description;
        if (current.Capacity != desired.Capacity) changes["capacity"] = desired.Capacity;
        if (current.RecommendedCapacity != desired.RecommendedCapacity)
            changes["recommendedCapacity"] = desired.RecommendedCapacity;
        if (!current.Tags.SequenceEqual(desired.Tags, StringComparer.Ordinal)) changes["tags"] = desired.Tags;

        WorldMetadataSnapshot updated = current;
        if (changes.Count > 0)
        {
            progress?.Invoke("Updating world fields on the VRChat server");
            string body = await SendJsonAsync(HttpMethod.Put, "worlds/" + current.Id, changes, cancellationToken);
            updated = DeserializeWorld(body);
        }

        if (!string.IsNullOrWhiteSpace(thumbnailPath))
        {
            progress?.Invoke("Preparing and uploading the new world image");
            string imageUrl = await UploadImageAsync(
                updated.ImageUrl,
                "World - " + updated.Title,
                thumbnailPath,
                progress,
                cancellationToken);
            string body = await SendJsonAsync(
                HttpMethod.Put,
                "worlds/" + current.Id,
                new Dictionary<string, string> { ["imageUrl"] = imageUrl },
                cancellationToken);
            updated = DeserializeWorld(body);
        }

        return updated;
    }

    public async Task<AvatarMetadataSnapshot> GetAvatarAsync(
        string avatarId,
        CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await client.GetAsync("avatars/" + avatarId, cancellationToken);
        string body = await ReadSuccessfulBodyAsync(response, cancellationToken);
        return DeserializeAvatar(body);
    }

    public async Task<AvatarMetadataSnapshot> UpdateAvatarAsync(
        AvatarMetadataSnapshot current,
        AvatarMetadataSnapshot desired,
        string? thumbnailPath,
        Action<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        EnsureOwner(current);
        Dictionary<string, object> changes = new();
        if (!string.Equals(current.Title, desired.Title, StringComparison.Ordinal)) changes["name"] = desired.Title;
        if (!string.Equals(current.Description, desired.Description, StringComparison.Ordinal)) changes["description"] = desired.Description;
        if (!current.Tags.SequenceEqual(desired.Tags, StringComparer.Ordinal)) changes["tags"] = desired.Tags;

        AvatarMetadataSnapshot updated = current;
        if (changes.Count > 0)
        {
            progress?.Invoke("Updating avatar fields on the VRChat server");
            string body = await SendJsonAsync(HttpMethod.Put, "avatars/" + current.Id, changes, cancellationToken);
            updated = DeserializeAvatar(body);
        }

        if (!string.IsNullOrWhiteSpace(thumbnailPath))
        {
            progress?.Invoke("Preparing and uploading the new avatar image");
            string imageUrl = await UploadImageAsync(
                updated.ImageUrl,
                "Avatar - " + updated.Title,
                thumbnailPath,
                progress,
                cancellationToken);
            string body = await SendJsonAsync(
                HttpMethod.Put,
                "avatars/" + current.Id,
                new Dictionary<string, string> { ["imageUrl"] = imageUrl },
                cancellationToken);
            updated = DeserializeAvatar(body);
        }

        return updated;
    }

    public void EnsureOwner(WorldMetadataSnapshot world)
    {
        if (CurrentUser == null) throw new VrchatApiException("Authenticate before accessing world metadata.");
        if (!string.Equals(world.AuthorId, CurrentUser.Id, StringComparison.Ordinal))
            throw new VrchatApiException(
                $"World {world.Id} belongs to another account. Signed in as {CurrentUser.DisplayName} ({CurrentUser.Id}).");
    }

    public void EnsureOwner(AvatarMetadataSnapshot avatar)
    {
        if (CurrentUser == null) throw new VrchatApiException("Authenticate before accessing avatar metadata.");
        if (!string.Equals(avatar.AuthorId, CurrentUser.Id, StringComparison.Ordinal))
            throw new VrchatApiException(
                $"Avatar {avatar.Id} belongs to another account. Signed in as {CurrentUser.DisplayName} ({CurrentUser.Id}).");
    }

    public static IReadOnlyList<MetadataChange> Compare(
        WorldMetadataSnapshot before,
        WorldMetadataSnapshot after,
        string? thumbnailPath = null)
    {
        List<MetadataChange> changes = [];
        Add(changes, "Title", before.Title, after.Title);
        Add(changes, "Description", before.Description, after.Description);
        Add(changes, "Maximum capacity", before.Capacity.ToString(), after.Capacity.ToString());
        Add(changes, "Recommended capacity", before.RecommendedCapacity.ToString(), after.RecommendedCapacity.ToString());
        Add(changes, "Tags", FormatTags(before.Tags), FormatTags(after.Tags));
        if (!string.IsNullOrWhiteSpace(thumbnailPath))
            changes.Add(new MetadataChange("Thumbnail", before.ImageUrl ?? "(none)", Path.GetFullPath(thumbnailPath)));
        return changes;
    }

    public static IReadOnlyList<MetadataChange> Compare(
        AvatarMetadataSnapshot before,
        AvatarMetadataSnapshot after,
        string? thumbnailPath = null)
    {
        List<MetadataChange> changes = [];
        Add(changes, "Title", before.Title, after.Title);
        Add(changes, "Description", before.Description, after.Description);
        Add(changes, "Tags", FormatTags(before.Tags), FormatTags(after.Tags));
        if (!string.IsNullOrWhiteSpace(thumbnailPath))
            changes.Add(new MetadataChange("Thumbnail", before.ImageUrl ?? "(none)", Path.GetFullPath(thumbnailPath)));
        return changes;
    }

    public void Dispose()
    {
        if (ownsClient) client.Dispose();
    }

    private async Task<string> UploadImageAsync(
        string? currentImageUrl,
        string displayName,
        string path,
        Action<string>? progress,
        CancellationToken cancellationToken)
    {
        string extension = Path.GetExtension(path).ToLowerInvariant();
        string mimeType = extension switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpg",
            _ => throw new VrchatApiException("Thumbnails must be PNG or JPEG files.")
        };
        string? fileId = ParseFileId(currentImageUrl);
        RemoteFile file;
        if (fileId == null)
        {
            file = await SendJsonAsync<RemoteFile>(HttpMethod.Post, "file", new
            {
                name = displayName + " - Image - VRCLI",
                mimeType,
                extension
            }, cancellationToken);
        }
        else
        {
            file = await GetJsonAsync<RemoteFile>("file/" + fileId, cancellationToken);
        }

        int latestIndex = LatestVersionIndex(file);
        if (latestIndex >= 0 && IsInterrupted(file.Versions![latestIndex]))
        {
            progress?.Invoke("Cleaning up an incomplete previous image upload");
            await SendNoContentAsync(HttpMethod.Delete, $"file/{file.Id}/{latestIndex}", null, cancellationToken);
            file = await GetJsonAsync<RemoteFile>("file/" + file.Id, cancellationToken);
        }

        byte[] fileBytes = await File.ReadAllBytesAsync(path, cancellationToken);
        byte[] signatureBytes = CreateRsyncSignature(fileBytes);
        byte[] fileMd5 = MD5.HashData(fileBytes);
        byte[] signatureMd5 = MD5.HashData(signatureBytes);
        file = await SendJsonAsync<RemoteFile>(HttpMethod.Post, "file/" + file.Id, new
        {
            signatureMd5 = Convert.ToBase64String(signatureMd5),
            signatureSizeInBytes = signatureBytes.Length,
            fileMd5 = Convert.ToBase64String(fileMd5),
            fileSizeInBytes = fileBytes.Length
        }, cancellationToken);

        int version = LatestVersionIndex(file);
        if (version < 0) throw new VrchatApiException("VRChat did not create an image file version.");
        RemoteFileVersion entry = file.Versions![version];
        await UploadDescriptorAsync(file.Id!, version, "file", entry.File!, fileBytes, mimeType, fileMd5, progress, cancellationToken);
        await UploadDescriptorAsync(
            file.Id!, version, "signature", entry.Signature!, signatureBytes,
            "application/x-rsync-signature", signatureMd5, progress, cancellationToken);

        for (int attempt = 0; attempt < 12; attempt++)
        {
            file = await GetJsonAsync<RemoteFile>("file/" + file.Id, cancellationToken);
            RemoteFileVersion latest = file.Versions![LatestVersionIndex(file)]
                                       ?? throw new VrchatApiException("VRChat returned an empty file version.");
            RemoteFileDescriptor? completedFile = latest.File;
            if (completedFile != null &&
                string.Equals(completedFile.Status, "complete", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(latest.Signature?.Status, "complete", StringComparison.OrdinalIgnoreCase))
                return completedFile.Url ?? throw new VrchatApiException("The uploaded image has no file URL.");
            if (string.Equals(latest.Status, "error", StringComparison.OrdinalIgnoreCase))
                throw new VrchatApiException("VRChat reported an error while processing the content image.");
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }

        throw new VrchatApiException("Timed out while VRChat was processing the content image.");
    }

    private async Task UploadDescriptorAsync(
        string fileId,
        int version,
        string kind,
        RemoteFileDescriptor descriptor,
        byte[] bytes,
        string mimeType,
        byte[] md5,
        Action<string>? progress,
        CancellationToken cancellationToken)
    {
        progress?.Invoke("Uploading image " + kind);
        if (string.Equals(descriptor.Category, "simple", StringComparison.OrdinalIgnoreCase))
        {
            using JsonDocument start = await SendJsonDocumentAsync(
                HttpMethod.Put, $"file/{fileId}/{version}/{kind}/start", null, cancellationToken);
            string url = start.RootElement.GetProperty("url").GetString()
                         ?? throw new VrchatApiException("VRChat returned an empty file upload URL.");
            using ByteArrayContent content = new(bytes);
            content.Headers.ContentType = new MediaTypeHeaderValue(mimeType);
            content.Headers.ContentMD5 = md5;
            using HttpResponseMessage upload = await client.PutAsync(url, content, cancellationToken);
            await ReadSuccessfulBodyAsync(upload, cancellationToken);
            await SendNoContentAsync(HttpMethod.Put, $"file/{fileId}/{version}/{kind}/finish", null, cancellationToken);
            return;
        }

        const int partSize = 100 * 1024 * 1024;
        List<string> etags = [];
        int parts = Math.Max(1, (bytes.Length + partSize - 1) / partSize);
        for (int part = 1; part <= parts; part++)
        {
            using JsonDocument start = await SendJsonDocumentAsync(
                HttpMethod.Put, $"file/{fileId}/{version}/{kind}/start?partNumber={part}", null, cancellationToken);
            string url = start.RootElement.GetProperty("url").GetString()
                         ?? throw new VrchatApiException("VRChat returned an empty multipart upload URL.");
            int offset = (part - 1) * partSize;
            int count = Math.Min(partSize, bytes.Length - offset);
            using ByteArrayContent content = new(bytes, offset, count);
            content.Headers.ContentType = new MediaTypeHeaderValue(mimeType);
            using HttpResponseMessage upload = await client.PutAsync(url, content, cancellationToken);
            await ReadSuccessfulBodyAsync(upload, cancellationToken);
            string? etag = upload.Headers.ETag?.Tag.Trim('"', '\'');
            if (!string.IsNullOrWhiteSpace(etag)) etags.Add(etag);
        }
        await SendNoContentAsync(
            HttpMethod.Put, $"file/{fileId}/{version}/{kind}/finish", new { etags }, cancellationToken);
    }

    public static byte[] CreateRsyncSignature(ReadOnlySpan<byte> file)
    {
        const int blockLength = 2048;
        const int strongLength = 32;
        int blocks = (file.Length + blockLength - 1) / blockLength;
        byte[] signature = new byte[12 + blocks * (4 + strongLength)];
        BinaryPrimitives.WriteUInt32BigEndian(signature.AsSpan(0, 4), 0x72730137);
        BinaryPrimitives.WriteUInt32BigEndian(signature.AsSpan(4, 4), blockLength);
        BinaryPrimitives.WriteUInt32BigEndian(signature.AsSpan(8, 4), strongLength);
        int outputOffset = 12;
        for (int offset = 0; offset < file.Length; offset += blockLength)
        {
            ReadOnlySpan<byte> block = file.Slice(offset, Math.Min(blockLength, file.Length - offset));
            BinaryPrimitives.WriteUInt32BigEndian(signature.AsSpan(outputOffset, 4), Rollsum(block));
            Blake2b.ComputeHash(strongLength, block).CopyTo(signature, outputOffset + 4);
            outputOffset += 4 + strongLength;
        }
        return signature;
    }

    private static uint Rollsum(ReadOnlySpan<byte> bytes)
    {
        ulong s1 = 0;
        ulong s2 = 0;
        foreach (byte value in bytes)
        {
            s1 += value + 31u;
            s2 += s1;
        }
        return (uint)((s2 << 16) | (s1 & 0xffff));
    }

    private async Task<T> GetJsonAsync<T>(string endpoint, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await client.GetAsync(endpoint, cancellationToken);
        string body = await ReadSuccessfulBodyAsync(response, cancellationToken);
        return JsonSerializer.Deserialize<T>(body, JsonOptions)
               ?? throw new VrchatApiException("VRChat returned an empty response.");
    }

    private async Task<string> GetWithRetryAsync(
        string endpoint,
        Action<string>? progress,
        CancellationToken cancellationToken)
    {
        const int attempts = 4;
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                using HttpResponseMessage response = await client.GetAsync(endpoint, cancellationToken);
                string body = await response.Content.ReadAsStringAsync(cancellationToken);
                if (response.IsSuccessStatusCode) return body;

                int statusCode = (int)response.StatusCode;
                if (!IsTransient(statusCode) || attempt == attempts)
                    throw new VrchatApiException(ReadError(body, statusCode), statusCode);
            }
            catch (HttpRequestException) when (attempt < attempts)
            {
            }

            TimeSpan delay = TimeSpan.FromSeconds(Math.Pow(2, attempt - 1));
            progress?.Invoke($"VRChat verification request was transiently unavailable; retrying in {delay.TotalSeconds:0} seconds ({attempt}/{attempts - 1}).");
            await delayAsync(delay, cancellationToken);
        }
    }

    private async Task<T> SendJsonAsync<T>(
        HttpMethod method,
        string endpoint,
        object body,
        CancellationToken cancellationToken)
    {
        string json = await SendJsonAsync(method, endpoint, body, cancellationToken);
        return JsonSerializer.Deserialize<T>(json, JsonOptions)
               ?? throw new VrchatApiException("VRChat returned an empty response.");
    }

    private async Task<string> SendJsonAsync(
        HttpMethod method,
        string endpoint,
        object body,
        CancellationToken cancellationToken)
    {
        using JsonDocument document = await SendJsonDocumentAsync(method, endpoint, body, cancellationToken);
        return document.RootElement.GetRawText();
    }

    private async Task<JsonDocument> SendJsonDocumentAsync(
        HttpMethod method,
        string endpoint,
        object? body,
        CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(method, endpoint);
        if (body != null)
            request.Content = new StringContent(JsonSerializer.Serialize(body, JsonOptions), Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await client.SendAsync(request, cancellationToken);
        string responseBody = await ReadSuccessfulBodyAsync(response, cancellationToken);
        return string.IsNullOrWhiteSpace(responseBody) ? JsonDocument.Parse("{}") : ParseJson(responseBody, "API");
    }

    private async Task SendNoContentAsync(
        HttpMethod method,
        string endpoint,
        object? body,
        CancellationToken cancellationToken)
    {
        using JsonDocument ignored = await SendJsonDocumentAsync(method, endpoint, body, cancellationToken);
    }

    private static async Task<string> ReadSuccessfulBodyAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        string body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new VrchatApiException(ReadError(body, (int)response.StatusCode), (int)response.StatusCode);
        return body;
    }

    private static bool IsTransient(int statusCode) =>
        statusCode == 429 || statusCode is 500 or 502 or 503 or 504;

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static WorldMetadataSnapshot DeserializeWorld(string json)
    {
        WorldResponse world = JsonSerializer.Deserialize<WorldResponse>(json, JsonOptions)
                              ?? throw new VrchatApiException("VRChat returned an empty world record.");
        if (string.IsNullOrWhiteSpace(world.Id) || string.IsNullOrWhiteSpace(world.AuthorId))
            throw new VrchatApiException("VRChat returned an incomplete world record.");
        return new WorldMetadataSnapshot(
            world.Id,
            world.AuthorId,
            world.Name ?? string.Empty,
            world.Description ?? string.Empty,
            world.Capacity,
            world.RecommendedCapacity,
            world.Tags ?? [],
            world.ImageUrl,
            world.Version);
    }

    private static AvatarMetadataSnapshot DeserializeAvatar(string json)
    {
        AvatarResponse avatar = JsonSerializer.Deserialize<AvatarResponse>(json, JsonOptions)
                                ?? throw new VrchatApiException("VRChat returned an empty avatar record.");
        if (string.IsNullOrWhiteSpace(avatar.Id) || string.IsNullOrWhiteSpace(avatar.AuthorId))
            throw new VrchatApiException("VRChat returned an incomplete avatar record.");
        return new AvatarMetadataSnapshot(
            avatar.Id,
            avatar.AuthorId,
            avatar.Name ?? string.Empty,
            avatar.Description ?? string.Empty,
            avatar.Tags ?? [],
            avatar.ImageUrl,
            avatar.Version);
    }

    private static VrchatUser? ReadUser(JsonElement root)
    {
        string? id = root.TryGetProperty("id", out JsonElement idElement) ? idElement.GetString() : null;
        string? displayName = root.TryGetProperty("displayName", out JsonElement display) ? display.GetString() : null;
        return id?.StartsWith("usr_", StringComparison.Ordinal) == true && !string.IsNullOrWhiteSpace(displayName)
            ? new VrchatUser(id, displayName)
            : null;
    }

    private static string[] ReadTwoFactorMethods(JsonElement root) =>
        root.TryGetProperty("requiresTwoFactorAuth", out JsonElement required) && required.ValueKind == JsonValueKind.Array
            ? required.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value!)
                .ToArray()
            : [];

    private static JsonDocument ParseJson(string body, string purpose)
    {
        try
        {
            return JsonDocument.Parse(body);
        }
        catch (JsonException exception)
        {
            throw new VrchatApiException($"VRChat returned an invalid {purpose} response: {exception.Message}");
        }
    }

    private static string ReadError(string body, int statusCode)
    {
        try
        {
            using JsonDocument json = JsonDocument.Parse(body);
            if (json.RootElement.TryGetProperty("error", out JsonElement error))
            {
                if (error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out JsonElement message))
                    return Clean(message.GetString()) ?? "VRChat API request failed (HTTP " + statusCode + ").";
                if (error.ValueKind == JsonValueKind.String)
                    return Clean(error.GetString()) ?? "VRChat API request failed (HTTP " + statusCode + ").";
            }
        }
        catch (JsonException)
        {
        }
        return "VRChat API request failed (HTTP " + statusCode + ").";
    }

    private static string? Clean(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return null;
        string clean = message.Trim();
        return clean.Length >= 2 && clean[0] == '"' && clean[^1] == '"' ? clean[1..^1] : clean;
    }

    private static int LatestVersionIndex(RemoteFile file) => (file.Versions?.Count ?? 0) - 1;

    private static bool IsInterrupted(RemoteFileVersion version) =>
        new[] { version.Status, version.File?.Status, version.Signature?.Status }
            .Any(status => string.Equals(status, "queued", StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(status, "error", StringComparison.OrdinalIgnoreCase));

    private static string? ParseFileId(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        foreach (string segment in Uri.UnescapeDataString(url).Split('/', StringSplitOptions.RemoveEmptyEntries))
            if (segment.StartsWith("file_", StringComparison.Ordinal)) return segment;
        return null;
    }

    private static void Add(List<MetadataChange> changes, string field, string before, string after)
    {
        if (!string.Equals(before, after, StringComparison.Ordinal)) changes.Add(new MetadataChange(field, before, after));
    }

    private static string FormatTags(IReadOnlyList<string> tags) => tags.Count == 0 ? "(none)" : string.Join(", ", tags);

    private sealed record WorldResponse(
        string Id,
        string AuthorId,
        string? Name,
        string? Description,
        int Capacity,
        int RecommendedCapacity,
        IReadOnlyList<string>? Tags,
        string? ImageUrl,
        int Version);

    private sealed record AvatarResponse(
        string Id,
        string AuthorId,
        string? Name,
        string? Description,
        IReadOnlyList<string>? Tags,
        string? ImageUrl,
        int Version);

    private sealed record RemoteFile(
        string? Id,
        IReadOnlyList<RemoteFileVersion>? Versions);

    private sealed record RemoteFileVersion(
        string? Status,
        RemoteFileDescriptor? File,
        RemoteFileDescriptor? Signature);

    private sealed record RemoteFileDescriptor(
        string? Status,
        string? Url,
        string? Category);
}
