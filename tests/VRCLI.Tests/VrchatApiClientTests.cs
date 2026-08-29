using System.Net;
using System.Text;
using System.Text.Json;
using KibaLab.WorldDeployment;

namespace WorldDeployment.Tests;

public sealed class VrchatApiClientTests
{
    [Fact]
    public void RsyncSignatureMatchesTheSdkImplementation()
    {
        byte[] signature = VrchatApiClient.CreateRsyncSignature(Encoding.UTF8.GetBytes("hello world"));

        Assert.Equal(
            "cnMBNwAACAAAAAAgIf4FsSVsg7KXEU0gGzAXnz8O8MrOl4NiLaWXQya0NheK7vYQ",
            Convert.ToBase64String(signature));
    }

    [Fact]
    public async Task ReusesAuthenticatedSessionForWorldReadAndUpdate()
    {
        RecordingHandler handler = new();
        using VrchatApiClient api = new(handler);

        VrchatLoginChallenge login = await api.BeginLoginAsync("user@example.com", "password");
        WorldMetadataSnapshot before = await api.GetWorldAsync("wrld_example");
        WorldMetadataSnapshot desired = before with { Title = "After", Capacity = 48 };
        WorldMetadataSnapshot after = await api.UpdateWorldAsync(before, desired, null);

        Assert.False(login.RequiresTwoFactor);
        Assert.Equal("After", after.Title);
        Assert.Equal(48, after.Capacity);
        Assert.Equal(3, handler.Requests.Count);
        Assert.Contains("\"name\":\"After\"", handler.Requests[2].Body);
        Assert.Contains("\"capacity\":48", handler.Requests[2].Body);
    }

    [Fact]
    public async Task MetadataNoOpSucceedsWithoutServerUpdate()
    {
        RecordingHandler handler = new();
        StringWriter output = new();
        MetadataApplication application = new(output, new StringWriter(), () => new VrchatApiClient(handler));
        DeployOptions options = Options() with
        {
            Title = "Before",
            TerminalMode = TerminalMode.Json
        };

        MetadataExecutionResult result = await application.RunAsync(options);

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.True(result.Result.Success);
        Assert.Empty(result.Result.Changes!);
        Assert.Equal(2, handler.Requests.Count);
        Assert.DoesNotContain(handler.Requests, request => request.Method == HttpMethod.Put);
        using JsonDocument json = JsonDocument.Parse(output.ToString());
        Assert.Contains("already up to date", json.RootElement.GetProperty("Message").GetString());
    }

    [Fact]
    public void ComputesReadableBeforeAndAfterChanges()
    {
        WorldMetadataSnapshot before = World(title: "Before", capacity: 32, recommended: 16);
        WorldMetadataSnapshot after = before with { Title = "After", Capacity = 64, RecommendedCapacity = 24 };

        IReadOnlyList<MetadataChange> changes = VrchatApiClient.Compare(before, after);

        Assert.Collection(
            changes,
            change => Assert.Equal(new MetadataChange("Title", "Before", "After"), change),
            change => Assert.Equal(new MetadataChange("Maximum capacity", "32", "64"), change),
            change => Assert.Equal(new MetadataChange("Recommended capacity", "16", "24"), change));
    }

    [Fact]
    public void MetadataOptionsMergeTagsAndPreserveUnspecifiedValues()
    {
        WorldMetadataSnapshot before = World(title: "Before", capacity: 32, recommended: 16);
        DeployOptions options = Options() with
        {
            Title = "After",
            Tags = ["author_tag_social"],
            HasTags = true
        };

        WorldMetadataSnapshot desired = MetadataApplication.ApplyOptions(before, options);

        Assert.Equal("After", desired.Title);
        Assert.Equal(32, desired.Capacity);
        Assert.Equal(["system_approved", "author_tag_social"], desired.Tags);
    }

    [Fact]
    public void MetadataOptionsCanRemoveTags()
    {
        WorldMetadataSnapshot before = World(title: "Before", capacity: 32, recommended: 16) with
        {
            Tags = ["system_approved", "author_tag_social"]
        };
        DeployOptions options = Options() with
        {
            RemovedTags = ["author_tag_social"],
            HasRemovedTags = true
        };

        WorldMetadataSnapshot desired = MetadataApplication.ApplyOptions(before, options);

        Assert.Equal(["system_approved"], desired.Tags);
    }

    [Fact]
    public async Task RetriesTransientContentReadsAndParsesPlatformPackages()
    {
        TransientContentHandler handler = new();
        using VrchatApiClient api = new(handler, (_, _) => Task.CompletedTask);

        RemoteContentSnapshot content = await api.GetContentAsync("avtr_example");

        Assert.Equal(3, handler.Attempts);
        Assert.Equal("avtr_example", content.Id);
        Assert.Equal(8, content.Version);
        RemotePlatformPackage package = Assert.Single(content.Packages);
        Assert.Equal("standalonewindows", package.Platform);
        Assert.Equal("https://files.example/avatar", package.AssetUrl);
    }

    [Fact]
    public async Task VerifiesTheUploadedOwnerVersionAndPlatform()
    {
        using VrchatApiClient api = new(new ReadyContentHandler());
        DeploymentResult result = new(
            true,
            ExitCodes.Success,
            "wrld_example",
            false,
            "Android",
            "complete",
            "Uploaded.",
            ServerVersion: 8);

        DeploymentVerification verification = await new DeploymentVerifier().VerifyAsync(
            api,
            result,
            "usr_owner",
            BuildPlatform.Android);

        Assert.True(verification.Success);
        Assert.Contains("version 8", verification.Message);
    }

    private static WorldMetadataSnapshot World(string title, int capacity, int recommended) => new(
        "wrld_example",
        "usr_owner",
        title,
        "Description",
        capacity,
        recommended,
        ["system_approved"],
        "https://api.vrchat.cloud/api/1/file/file_example/1/file",
        7);

    private static DeployOptions Options() => new(
        OperationMode.Meta,
        Directory.GetCurrentDirectory(),
        "wrld_example",
        false,
        null,
        null,
        null,
        32,
        16,
        false,
        false,
        [],
        false,
        [],
        false,
        null,
        "owner",
        "password",
        BuildPlatform.StandaloneWindows64,
        null,
        null,
        null,
        null,
        null,
        null,
        false,
        TimeSpan.FromMinutes(1),
        false,
        false,
        false,
        TerminalMode.Plain);

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Path, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            string body = request.Content == null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request.Method, request.RequestUri!.AbsolutePath, body));

            string response = request.RequestUri.AbsolutePath switch
            {
                "/api/1/auth/user" => "{\"id\":\"usr_owner\",\"displayName\":\"Owner\"}",
                "/api/1/worlds/wrld_example" when request.Method == HttpMethod.Get => WorldJson("Before", 32),
                "/api/1/worlds/wrld_example" => WorldJson("After", 48),
                _ => throw new InvalidOperationException("Unexpected request: " + request.RequestUri)
            };
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response, Encoding.UTF8, "application/json")
            };
        }

        private static string WorldJson(string title, int capacity) => JsonSerializer.Serialize(new
        {
            id = "wrld_example",
            authorId = "usr_owner",
            name = title,
            description = "Description",
            capacity,
            recommendedCapacity = 16,
            tags = new[] { "system_approved" },
            imageUrl = "https://api.vrchat.cloud/api/1/file/file_example/1/file",
            version = 7
        });
    }

    private sealed class TransientContentHandler : HttpMessageHandler
    {
        public int Attempts { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Attempts++;
            if (Attempts < 3)
            {
                HttpStatusCode status = Attempts == 1
                    ? HttpStatusCode.ServiceUnavailable
                    : HttpStatusCode.TooManyRequests;
                return Task.FromResult(new HttpResponseMessage(status)
                {
                    Content = new StringContent("{\"error\":{\"message\":\"retry\"}}")
                });
            }

            return Task.FromResult(JsonResponse("""
            {
              "id": "avtr_example",
              "authorId": "usr_owner",
              "version": 8,
              "unityPackages": [
                {
                  "platform": "standalonewindows",
                  "assetUrl": "https://files.example/avatar",
                  "assetVersion": 8
                }
              ]
            }
            """));
        }
    }

    private sealed class ReadyContentHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(JsonResponse("""
            {
              "id": "wrld_example",
              "authorId": "usr_owner",
              "version": 8,
              "unityPackages": [
                {
                  "platform": "android",
                  "assetUrl": "https://files.example/world",
                  "assetVersion": 8
                }
              ]
            }
            """));
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };
}
