using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace KibaLab.VRCLI;

public sealed record VrchatCredentialValidationResult(
    string? DisplayName,
    string? UserId,
    IReadOnlyList<string> RequiredTwoFactorMethods)
{
    public bool IsFullyAuthenticated => RequiredTwoFactorMethods.Count == 0;
}

public sealed class VrchatCredentialException(string message) : Exception(message);

public static class VrchatCredentialValidator
{
    private static readonly Uri CurrentUserEndpoint = new("https://api.vrchat.cloud/api/1/auth/user");

    public static async Task<VrchatCredentialValidationResult> ValidateAsync(
        string username,
        string password,
        HttpMessageHandler? messageHandler = null,
        CancellationToken cancellationToken = default)
    {
        bool ownsHandler = messageHandler == null;
        messageHandler ??= new HttpClientHandler { UseProxy = false };
        using HttpClient client = new(messageHandler, ownsHandler) { Timeout = TimeSpan.FromMinutes(2) };
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "VRC.Core.BestHTTP");

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

        try
        {
            using HttpResponseMessage response = await client.GetAsync(CurrentUserEndpoint, cancellationToken);
            string body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new VrchatCredentialException(ReadError(body, (int)response.StatusCode));

            using JsonDocument json = JsonDocument.Parse(body);
            JsonElement root = json.RootElement;
            string[] methods = root.TryGetProperty("requiresTwoFactorAuth", out JsonElement required) &&
                               required.ValueKind == JsonValueKind.Array
                ? required.EnumerateArray()
                    .Where(item => item.ValueKind == JsonValueKind.String)
                    .Select(item => item.GetString())
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Select(value => value!)
                    .ToArray()
                : [];
            string? displayName = root.TryGetProperty("displayName", out JsonElement display)
                ? display.GetString()
                : null;
            string? userId = root.TryGetProperty("id", out JsonElement id) ? id.GetString() : null;

            bool hasCompleteUser = !string.IsNullOrWhiteSpace(displayName) &&
                                   userId?.StartsWith("usr_", StringComparison.Ordinal) == true;
            bool hasAuthenticationCookie = response.Headers.TryGetValues("Set-Cookie", out IEnumerable<string>? cookies) &&
                                           cookies.Any(cookie => cookie.StartsWith("auth=", StringComparison.OrdinalIgnoreCase));
            if (methods.Length == 0 && !hasCompleteUser)
            {
                throw new VrchatCredentialException(
                    "VRChat did not return a verified user for these credentials.");
            }
            if (methods.Length > 0 && !hasAuthenticationCookie)
            {
                throw new VrchatCredentialException(
                    "VRChat returned an incomplete two-factor challenge. Check the username/email and password, then try again.");
            }
            return new VrchatCredentialValidationResult(displayName, userId, methods);
        }
        catch (VrchatCredentialException)
        {
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException)
        {
            throw new VrchatCredentialException("Unable to validate the VRChat account: " + exception.Message);
        }
    }

    private static string ReadError(string body, int statusCode)
    {
        try
        {
            using JsonDocument json = JsonDocument.Parse(body);
            if (json.RootElement.TryGetProperty("error", out JsonElement error))
            {
                if (error.ValueKind == JsonValueKind.Object &&
                    error.TryGetProperty("message", out JsonElement message) &&
                    !string.IsNullOrWhiteSpace(message.GetString()))
                    return CleanMessage(message.GetString()!);
                if (error.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(error.GetString()))
                    return CleanMessage(error.GetString()!);
            }
        }
        catch (JsonException)
        {
        }
        return "VRChat rejected the account credentials (HTTP " + statusCode + ").";
    }

    private static string CleanMessage(string message)
    {
        string cleaned = message.Trim();
        if (cleaned.Length >= 2 && cleaned[0] == '"' && cleaned[^1] == '"')
            cleaned = cleaned[1..^1];
        return cleaned;
    }
}
