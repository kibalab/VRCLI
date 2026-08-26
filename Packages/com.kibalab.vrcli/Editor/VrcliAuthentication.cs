using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using BestHTTP.JSON;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using VRC;
using VRC.Core;
using VRC.SDKBase;
using VRC.SDKBase.Editor;
using VRC.SDKBase.Editor.Api;

namespace KibaLab.VRCLI.Editor
{
    internal static class VrcliAuthentication
    {
        private static readonly Uri ApiRoot = new Uri("https://api.vrchat.cloud");
        private static readonly Uri CurrentUserEndpoint = new Uri(ApiRoot, "/api/1/auth/user");

        public static async Task LoginAsync(VrcliRequest request)
        {
            VrcliLog.Phase("AUTH", "Initializing VRChat SDK authentication.");
            API.SetOnlineMode(true);
            VRCSdkControlPanel.RefreshApiUrlSetting();

            if (!ConfigManager.RemoteConfig.IsInitialized())
            {
                TaskCompletionSource<bool> configReady = new TaskCompletionSource<bool>();
                ConfigManager.RemoteConfig.Init(() => configReady.TrySetResult(true));
                await WithTimeout(configReady.Task, TimeSpan.FromMinutes(2), "VRChat remote configuration timed out.");
            }
            VrcliLog.Info("AUTH", "VRChat remote configuration is ready.");

            if (await TryResumeSdkSessionAsync(request)) return;
            VrcliLog.Info("AUTH", "No valid matching SDK session was found; starting credential login.");

            CookieContainer cookies = new CookieContainer();
            string currentUserJson;
            using (HttpClientHandler handler = new HttpClientHandler
            {
                CookieContainer = cookies,
                UseProxy = false
            })
            using (HttpClient client = new HttpClient(handler))
            {
                client.Timeout = TimeSpan.FromMinutes(2);
                AddSdkHeaders(client);

                byte[] credentialBytes = Encoding.UTF8.GetBytes(request.Username + ":" + request.Password);
                try
                {
                    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                        "Basic",
                        Convert.ToBase64String(credentialBytes));
                }
                finally
                {
                    Array.Clear(credentialBytes, 0, credentialBytes.Length);
                }

                currentUserJson = await GetCurrentUserAsync(client);
                VrcliLog.Info("AUTH", "Primary credentials were accepted by VRChat.");
                string[] methods = ReadTwoFactorMethods(currentUserJson);
                if (methods.Length > 0)
                {
                    VrcliLog.Info("AUTH", "Two-factor authentication required: " + string.Join(", ", methods) + ".");
                    client.DefaultRequestHeaders.Authorization = null;
                    string twoFactorCode = request.TwoFactorCode;
                    if (!string.IsNullOrWhiteSpace(request.TotpSecret))
                    {
                        if (!methods.Contains("totp", StringComparer.OrdinalIgnoreCase))
                        {
                            throw new VrcliAuthenticationException(
                                "A TOTP secret was provided, but VRChat requested a different two-factor method (" +
                                string.Join(", ", methods) + ").");
                        }

                        await WaitForSafeTotpWindowAsync();
                        try
                        {
                            twoFactorCode = VrcliTotpGenerator.GenerateCode(
                                request.TotpSecret,
                                DateTimeOffset.UtcNow);
                        }
                        catch (ArgumentException exception)
                        {
                            throw new VrcliAuthenticationException(
                                "The TOTP secret is invalid: " + exception.Message);
                        }
                        methods = new[] { "totp" };
                        VrcliLog.Info("AUTH", "Generated a time-based one-time code in memory; the value will not be logged.");
                    }

                    if (string.IsNullOrWhiteSpace(twoFactorCode))
                    {
                        InteractiveTwoFactorResponse interactive = RequestInteractiveTwoFactor(methods);
                        if (interactive != null)
                        {
                            twoFactorCode = interactive.Code;
                            methods = new[] { interactive.Method };
                            VrcliLog.Info("AUTH", "Received interactive verification input; the value will not be logged.");
                        }
                    }

                    if (string.IsNullOrWhiteSpace(twoFactorCode))
                    {
                        throw new VrcliAuthenticationException(
                            "Two-factor authentication is required (" + string.Join(", ", methods) + "). " +
                            "Use interactive authentication or provide VRCLI_TWO_FACTOR_CODE/VRCLI_TOTP_SECRET.");
                    }

                    VrcliLog.Info("AUTH", "Submitting two-factor verification using " + DescribeTwoFactorMethod(methods) + ".");
                    await VerifyTwoFactorAsync(client, methods, twoFactorCode);
                    VrcliLog.Info("AUTH", "Two-factor verification succeeded.");
                    currentUserJson = await GetCurrentUserAsync(client);
                    if (ReadTwoFactorMethods(currentUserJson).Length > 0)
                    {
                        throw new VrcliAuthenticationException(
                            "VRChat still requires two-factor authentication after verification.");
                    }
                }
            }

            CookieCollection established = cookies.GetCookies(ApiRoot);
            Cookie auth = established["auth"];
            if (auth == null || string.IsNullOrWhiteSpace(auth.Value))
            {
                throw new VrcliAuthenticationException("VRChat did not issue an authentication cookie.");
            }

            Cookie twoFactor = established["twoFactorAuth"];
            if (twoFactor == null || string.IsNullOrWhiteSpace(twoFactor.Value))
                ApiCredentials.Set(request.Username, request.Username, "vrchat", auth.Value);
            else
                ApiCredentials.Set(request.Username, request.Username, "vrchat", auth.Value, twoFactor.Value);

            APIUser authenticated = CreateCurrentUser(currentUserJson);
            CompleteLogin(authenticated);
            VrcliLog.Info("AUTH", "Saved the refreshed VRChat SDK session for future deployments.");
            LogAuthenticatedUser(authenticated, "credential login");
        }

        private static async Task<bool> TryResumeSdkSessionAsync(VrcliRequest request)
        {
            VrcliLog.Info("AUTH", "Checking for a saved VRChat SDK session.");
            if (!ApiCredentials.Load() ||
                !string.Equals(ApiCredentials.GetHumanName(), request.Username, StringComparison.OrdinalIgnoreCase))
                return false;

            string authToken = ApiCredentials.GetAuthToken();
            if (string.IsNullOrWhiteSpace(authToken)) return false;

            try
            {
                CookieContainer cookies = new CookieContainer();
                cookies.Add(ApiRoot, new Cookie("auth", authToken));
                string twoFactorToken = ApiCredentials.GetTwoFactorAuthToken();
                if (!string.IsNullOrWhiteSpace(twoFactorToken))
                    cookies.Add(ApiRoot, new Cookie("twoFactorAuth", twoFactorToken));

                using (HttpClientHandler handler = new HttpClientHandler
                {
                    CookieContainer = cookies,
                    UseProxy = false
                })
                using (HttpClient client = new HttpClient(handler))
                {
                    client.Timeout = TimeSpan.FromMinutes(2);
                    AddSdkHeaders(client);
                    string currentUserJson = await GetCurrentUserAsync(client);
                    if (ReadTwoFactorMethods(currentUserJson).Length > 0) return false;
                    APIUser user = CreateCurrentUser(currentUserJson);
                    CompleteLogin(user);
                    LogAuthenticatedUser(user, "saved SDK session");
                    return true;
                }
            }
            catch (VrcliAuthenticationException)
            {
                VrcliLog.Info("AUTH", "The saved SDK session is no longer valid.");
                return false;
            }
        }

        private static void LogAuthenticatedUser(APIUser user, string method)
        {
            VrcliLog.Info("AUTH", "Authentication succeeded via " + method + ".");
            VrcliLog.Info("AUTH", "Publish permission confirmed for " + user.displayName + " (" + user.id + ").");
        }

        private static string DescribeTwoFactorMethod(string[] methods)
        {
            if (methods.Contains("emailOtp", StringComparer.OrdinalIgnoreCase)) return "email OTP";
            if (methods.Contains("totp", StringComparer.OrdinalIgnoreCase)) return "TOTP";
            if (methods.Contains("otp", StringComparer.OrdinalIgnoreCase)) return "recovery code";
            return "the requested method";
        }

        private static void AddSdkHeaders(HttpClient client)
        {
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "VRC.Core.BestHTTP");
            client.DefaultRequestHeaders.TryAddWithoutValidation("X-MacAddress", API.DeviceID);
            client.DefaultRequestHeaders.TryAddWithoutValidation("X-SDK-Version", Tools.SdkVersion);
            client.DefaultRequestHeaders.TryAddWithoutValidation("X-Platform", Tools.Platform);
            client.DefaultRequestHeaders.TryAddWithoutValidation("X-Unity-Version", Application.unityVersion);
            client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json");
        }

        private static async Task<string> GetCurrentUserAsync(HttpClient client)
        {
            using (HttpResponseMessage response = await client.GetAsync(CurrentUserEndpoint))
            {
                string body = await response.Content.ReadAsStringAsync();
                if (!response.IsSuccessStatusCode)
                    throw new VrcliAuthenticationException(ReadApiError(body, response.StatusCode));
                return body;
            }
        }

        private static string[] ReadTwoFactorMethods(string json)
        {
            try
            {
                JObject root = JObject.Parse(json);
                JArray required = root["requiresTwoFactorAuth"] as JArray;
                return required == null
                    ? new string[0]
                    : required.Values<string>()
                        .Where(value => !string.IsNullOrWhiteSpace(value))
                        .ToArray();
            }
            catch (JsonException exception)
            {
                throw new VrcliAuthenticationException(
                    "VRChat returned an invalid authentication response: " + exception.Message);
            }
        }

        private static async Task VerifyTwoFactorAsync(HttpClient client, string[] methods, string code)
        {
            string endpoint;
            if (methods.Contains("emailOtp", StringComparer.OrdinalIgnoreCase))
                endpoint = "/api/1/auth/twofactorauth/emailotp/verify";
            else if (methods.Contains("totp", StringComparer.OrdinalIgnoreCase))
                endpoint = "/api/1/auth/twofactorauth/totp/verify";
            else if (methods.Contains("otp", StringComparer.OrdinalIgnoreCase))
                endpoint = "/api/1/auth/twofactorauth/otp/verify";
            else
                throw new VrcliAuthenticationException(
                    "VRChat requested an unsupported two-factor authentication method: " +
                    string.Join(", ", methods));

            string payload = JsonConvert.SerializeObject(new { code = code });
            using (StringContent content = new StringContent(payload, Encoding.UTF8, "application/json"))
            using (HttpResponseMessage response = await client.PostAsync(new Uri(ApiRoot, endpoint), content))
            {
                string body = await response.Content.ReadAsStringAsync();
                if (!response.IsSuccessStatusCode)
                    throw new VrcliAuthenticationException(ReadApiError(body, response.StatusCode));
            }
        }

        private static async Task WaitForSafeTotpWindowAsync()
        {
            long seconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            int remaining = 30 - (int)(seconds % 30);
            if (remaining <= 3)
            {
                VrcliLog.Info("AUTH", "The current TOTP window is about to expire; waiting for the next window.");
                await Task.Delay(TimeSpan.FromSeconds(remaining + 1));
            }
        }

        private static InteractiveTwoFactorResponse RequestInteractiveTwoFactor(string[] methods)
        {
            string pipeName = Environment.GetEnvironmentVariable("VRCLI_TWO_FACTOR_PIPE");
            if (string.IsNullOrWhiteSpace(pipeName)) return null;

            VrcliLog.Info("AUTH", "Waiting for an interactive two-factor response.");
            try
            {
                using (NamedPipeClientStream pipe = new NamedPipeClientStream(
                    ".",
                    pipeName,
                    PipeDirection.InOut,
                    PipeOptions.None))
                {
                    pipe.Connect(30000);
                    using (StreamReader reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, true))
                    using (StreamWriter writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, true))
                    {
                        writer.AutoFlush = true;
                        writer.WriteLine(JsonConvert.SerializeObject(new InteractiveTwoFactorRequest
                        {
                            Methods = methods
                        }));
                        string responseJson = reader.ReadLine();
                        InteractiveTwoFactorResponse response = string.IsNullOrWhiteSpace(responseJson)
                            ? null
                            : JsonConvert.DeserializeObject<InteractiveTwoFactorResponse>(responseJson);
                        if (response == null || string.IsNullOrWhiteSpace(response.Code) ||
                            !methods.Contains(response.Method, StringComparer.OrdinalIgnoreCase))
                        {
                            throw new VrcliAuthenticationException("Interactive two-factor authentication was cancelled or invalid.");
                        }
                        return response;
                    }
                }
            }
            catch (IOException exception)
            {
                throw new VrcliAuthenticationException(
                    "Unable to communicate with the interactive two-factor prompt: " + exception.Message);
            }
            catch (TimeoutException)
            {
                throw new VrcliAuthenticationException("Timed out while opening the interactive two-factor prompt.");
            }
        }

        private static string ReadApiError(string body, HttpStatusCode statusCode)
        {
            try
            {
                JToken error = JObject.Parse(body)["error"];
                string message = error?["message"]?.Value<string>() ?? error?.Value<string>();
                if (!string.IsNullOrWhiteSpace(message)) return message.Trim().Trim('"');
            }
            catch (JsonException)
            {
            }
            return "VRChat authentication failed (" + (int)statusCode + " " + statusCode + ").";
        }

        private static APIUser CreateCurrentUser(string json)
        {
            Json.Token token = Json.Decode(json);
            Json.JObject fields = token.TryGetObject();
            if (fields == null)
            {
                throw new VrcliAuthenticationException("VRChat returned invalid current-user JSON.");
            }

            APIUser user = new APIUser();
            string parseError = null;
            IReadOnlyDictionary<string, Json.Token> apiFields = fields;
            if (!user.SetApiFieldsFromJson(apiFields, ref parseError))
            {
                throw new VrcliAuthenticationException(
                    string.IsNullOrWhiteSpace(parseError)
                        ? "VRChat current-user data could not be parsed by the SDK."
                        : parseError);
            }

            PropertyInfo currentUser = typeof(APIUser).GetProperty(
                "CurrentUser",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            MethodInfo setter = currentUser != null ? currentUser.GetSetMethod(true) : null;
            if (setter == null)
            {
                throw new VrcliAuthenticationException("This VRChat SDK does not expose its current-user session field.");
            }
            setter.Invoke(null, new object[] { user });
            return user;
        }

        private static void CompleteLogin(APIUser user)
        {
            if (user == null) throw new VrcliAuthenticationException("VRChat returned an empty user.");
            AnalyticsSDK.LoggedInUserChanged(user);
            if (!APIUser.IsLoggedIn || APIUser.CurrentUser == null)
            {
                throw new VrcliAuthenticationException("VRChat did not establish a logged-in SDK session.");
            }
            if (!APIUser.CurrentUser.canPublishWorldsAndAvatars)
            {
                throw new VrcliAuthenticationException("This VRChat account cannot publish worlds and avatars.");
            }
        }

        private static async Task WithTimeout(Task task, TimeSpan timeout, string message)
        {
            Task completed = await Task.WhenAny(task, Task.Delay(timeout));
            if (completed != task) throw new TimeoutException(message);
            await task;
        }

        private sealed class InteractiveTwoFactorRequest
        {
            public string[] Methods;
        }

        private sealed class InteractiveTwoFactorResponse
        {
            public string Method;
            public string Code;
        }

    }
}
