using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading.Tasks;
using BestHTTP.JSON;
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

            await UseCliSessionAsync(request);
        }

        private static async Task UseCliSessionAsync(DeploymentRequest request)
        {
            DeploymentLog.Info("AUTH", "Using the VRCLI session verified before Unity startup.");
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
            }
        }

        private static void LogAuthenticatedUser(APIUser user, string method)
        {
            DeploymentLog.Info("AUTH", "Authentication succeeded via " + method + ".");
            DeploymentLog.Info("AUTH", "Publish permission confirmed for " + user.displayName + " (" + user.id + ").");
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
            catch (Newtonsoft.Json.JsonException exception)
            {
                throw new LoginException(
                    "VRChat returned an invalid authentication response: " + exception.Message);
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
            catch (Newtonsoft.Json.JsonException)
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
