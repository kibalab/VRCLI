using System;
using System.Collections.Generic;
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

namespace KibaLab.WorldDeployment.Editor
{
    internal static class Authentication
    {
        private static readonly Uri ApiRoot = new Uri("https://api.vrchat.cloud");
        private static readonly Uri CurrentUserEndpoint = new Uri(ApiRoot, "/api/1/auth/user");

        public static async Task LoginAsync(DeploymentRequest request)
        {
            DeploymentLog.Phase("AUTH", "Initializing VRChat SDK authentication.");
            API.SetOnlineMode(true);
            VRCSdkControlPanel.RefreshApiUrlSetting();

            if (!ConfigManager.RemoteConfig.IsInitialized())
            {
                TaskCompletionSource<bool> configReady = new TaskCompletionSource<bool>();
                ConfigManager.RemoteConfig.Init(() => configReady.TrySetResult(true));
                await WithTimeout(configReady.Task, TimeSpan.FromMinutes(2), "VRChat remote configuration timed out.");
            }
            DeploymentLog.Info("AUTH", "VRChat remote configuration is ready.");

            if (await TryUseCliSessionAsync(request)) return;
            if (await TryResumeSdkSessionAsync(request)) return;
            DeploymentLog.Info("AUTH", "No valid matching SDK session was found; starting credential login.");

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
                DeploymentLog.Info("AUTH", "Primary credentials were accepted by VRChat.");
                string[] methods = ReadTwoFactorMethods(currentUserJson);
                if (methods.Length > 0)
                {
                    DeploymentLog.Info("AUTH", "Two-factor authentication required: " + string.Join(", ", methods) + ".");
                    client.DefaultRequestHeaders.Authorization = null;
                    string twoFactorCode = request.TwoFactorCode;
                    if (!string.IsNullOrWhiteSpace(request.TotpSecret))
                    {
                        if (!methods.Contains("totp", StringComparer.OrdinalIgnoreCase))
                        {
                            throw new LoginException(
                                "A TOTP secret was provided, but VRChat requested a different two-factor method (" +
                                string.Join(", ", methods) + ").");
                        }

                        await WaitForSafeTotpWindowAsync();
                        try
                        {
                            twoFactorCode = TotpGenerator.GenerateCode(
                                request.TotpSecret,
                                DateTimeOffset.UtcNow);
                        }
                        catch (ArgumentException exception)
                        {
                            throw new LoginException(
                                "The TOTP secret is invalid: " + exception.Message);
                        }
                        methods = new[] { "totp" };
                        DeploymentLog.Info("AUTH", "Generated a time-based one-time code in memory; the value will not be logged.");
                    }
                    else if (!string.IsNullOrWhiteSpace(twoFactorCode))
                    {
                        if (string.IsNullOrWhiteSpace(request.TwoFactorMethod) ||
                            !methods.Contains(request.TwoFactorMethod, StringComparer.OrdinalIgnoreCase))
                        {
                            throw new LoginException(
                                "The supplied two-factor code method is unavailable. VRChat requested: " +
                                string.Join(", ", methods) + ".");
                        }
                        methods = new[] { request.TwoFactorMethod };
                    }

                    if (string.IsNullOrWhiteSpace(twoFactorCode))
                    {
                        throw new LoginException(
                            "Two-factor authentication is required (" + string.Join(", ", methods) + "). " +
                            "Use interactive authentication or provide VRCLI_TWO_FACTOR_CODE/VRCLI_TOTP_SECRET.");
                    }

                    DeploymentLog.Info("AUTH", "Submitting two-factor verification using " + DescribeTwoFactorMethod(methods) + ".");
                    await VerifyTwoFactorAsync(client, methods, twoFactorCode);
                    DeploymentLog.Info("AUTH", "Two-factor verification succeeded.");
                    currentUserJson = await GetCurrentUserAsync(client);
                    if (ReadTwoFactorMethods(currentUserJson).Length > 0)
                    {
                        throw new LoginException(
                            "VRChat still requires two-factor authentication after verification.");
                    }
                }
            }

            CookieCollection established = cookies.GetCookies(ApiRoot);
            Cookie auth = established["auth"];
            if (auth == null || string.IsNullOrWhiteSpace(auth.Value))
            {
                throw new LoginException("VRChat did not issue an authentication cookie.");
            }

            Cookie twoFactor = established["twoFactorAuth"];
            if (twoFactor == null || string.IsNullOrWhiteSpace(twoFactor.Value))
                ApiCredentials.Set(request.Username, request.Username, "vrchat", auth.Value);
            else
                ApiCredentials.Set(request.Username, request.Username, "vrchat", auth.Value, twoFactor.Value);

            APIUser authenticated = CreateCurrentUser(currentUserJson);
            CompleteLogin(authenticated);
            DeploymentLog.Info("AUTH", "Saved the refreshed VRChat SDK session for future deployments.");
            LogAuthenticatedUser(authenticated, "credential login");
        }

        private static async Task<bool> TryUseCliSessionAsync(DeploymentRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.AuthToken)) return false;
            DeploymentLog.Info("AUTH", "Using the VRCLI session verified before Unity startup.");
            try
            {
                CookieContainer cookies = new CookieContainer();
                cookies.Add(ApiRoot, new Cookie("auth", request.AuthToken));
                if (!string.IsNullOrWhiteSpace(request.TwoFactorToken))
                    cookies.Add(ApiRoot, new Cookie("twoFactorAuth", request.TwoFactorToken));

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
                    if (ReadTwoFactorMethods(currentUserJson).Length > 0)
                        throw new LoginException("The pre-verified VRCLI session still requires two-factor authentication.");
                    APIUser user = CreateCurrentUser(currentUserJson);
                    if (string.IsNullOrWhiteSpace(request.TwoFactorToken))
                        ApiCredentials.Set(user.displayName, user.displayName, "vrchat", request.AuthToken);
                    else
                        ApiCredentials.Set(
                            user.displayName,
                            user.displayName,
                            "vrchat",
                            request.AuthToken,
                            request.TwoFactorToken);
                    CompleteLogin(user);
                    LogAuthenticatedUser(user, "pre-verified VRCLI session");
                    return true;
                }
            }
            catch (LoginException)
            {
                DeploymentLog.Info("AUTH", "The pre-verified VRCLI session is no longer valid.");
                return false;
            }
        }

        private static async Task<bool> TryResumeSdkSessionAsync(DeploymentRequest request)
        {
            DeploymentLog.Info("AUTH", "Checking for a saved VRChat SDK session.");
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
            catch (LoginException)
            {
                DeploymentLog.Info("AUTH", "The saved SDK session is no longer valid.");
                return false;
            }
        }

        private static void LogAuthenticatedUser(APIUser user, string method)
        {
            DeploymentLog.Info("AUTH", "Authentication succeeded via " + method + ".");
            DeploymentLog.Info("AUTH", "Publish permission confirmed for " + user.displayName + " (" + user.id + ").");
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
                    throw new LoginException(ReadApiError(body, response.StatusCode));
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
                throw new LoginException(
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
                throw new LoginException(
                    "VRChat requested an unsupported two-factor authentication method: " +
                    string.Join(", ", methods));

            string payload = JsonConvert.SerializeObject(new { code = code });
            using (StringContent content = new StringContent(payload, Encoding.UTF8, "application/json"))
            using (HttpResponseMessage response = await client.PostAsync(new Uri(ApiRoot, endpoint), content))
            {
                string body = await response.Content.ReadAsStringAsync();
                if (!response.IsSuccessStatusCode)
                    throw new LoginException(ReadApiError(body, response.StatusCode));
            }
        }

        private static async Task WaitForSafeTotpWindowAsync()
        {
            long seconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            int remaining = 30 - (int)(seconds % 30);
            if (remaining <= 3)
            {
                DeploymentLog.Info("AUTH", "The current TOTP window is about to expire; waiting for the next window.");
                await Task.Delay(TimeSpan.FromSeconds(remaining + 1));
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
                throw new LoginException("VRChat returned invalid current-user JSON.");
            }

            APIUser user = new APIUser();
            string parseError = null;
            IReadOnlyDictionary<string, Json.Token> apiFields = fields;
            if (!user.SetApiFieldsFromJson(apiFields, ref parseError))
            {
                throw new LoginException(
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
                throw new LoginException("This VRChat SDK does not expose its current-user session field.");
            }
            setter.Invoke(null, new object[] { user });
            return user;
        }

        private static void CompleteLogin(APIUser user)
        {
            if (user == null) throw new LoginException("VRChat returned an empty user.");
            AnalyticsSDK.LoggedInUserChanged(user);
            if (!APIUser.IsLoggedIn || APIUser.CurrentUser == null)
            {
                throw new LoginException("VRChat did not establish a logged-in SDK session.");
            }
            if (!APIUser.CurrentUser.canPublishWorldsAndAvatars)
            {
                throw new LoginException("This VRChat account cannot publish worlds and avatars.");
            }
        }

        private static async Task WithTimeout(Task task, TimeSpan timeout, string message)
        {
            Task completed = await Task.WhenAny(task, Task.Delay(timeout));
            if (completed != task) throw new TimeoutException(message);
            await task;
        }


    }
}
