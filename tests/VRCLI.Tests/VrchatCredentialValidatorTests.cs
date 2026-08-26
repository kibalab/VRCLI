using System.Net;
using System.Text;
using KibaLab.WorldDeployment;

namespace WorldDeployment.Tests;

public sealed class VrchatCredentialValidatorTests
{
    [Fact]
    public async Task AcceptsPrimaryCredentialsWhenTwoFactorIsRequired()
    {
        StubHandler handler = new(
            HttpStatusCode.OK,
            "{\"requiresTwoFactorAuth\":[\"totp\",\"otp\"]}",
            "auth=temporary-session; Path=/; Secure; HttpOnly");

        VrchatCredentialValidationResult result = await VrchatCredentialValidator.ValidateAsync(
            "account@example.com",
            "password",
            handler);

        Assert.Equal(["totp", "otp"], result.RequiredTwoFactorMethods);
        Assert.StartsWith("Basic ", handler.Authorization);
    }

    [Fact]
    public async Task AcceptsACompleteAuthenticatedUserWithoutTwoFactor()
    {
        StubHandler handler = new(
            HttpStatusCode.OK,
            "{\"id\":\"usr_1234\",\"displayName\":\"KIBA_\"}");

        VrchatCredentialValidationResult result = await VrchatCredentialValidator.ValidateAsync(
            "KIBA_",
            "password",
            handler);

        Assert.True(result.IsFullyAuthenticated);
        Assert.Equal("usr_1234", result.UserId);
    }

    [Fact]
    public async Task RejectsAnIncompleteTwoFactorShapedResponse()
    {
        StubHandler handler = new(HttpStatusCode.OK, "{\"requiresTwoFactorAuth\":[\"totp\"]}");

        VrchatCredentialException exception = await Assert.ThrowsAsync<VrchatCredentialException>(() =>
            VrchatCredentialValidator.ValidateAsync("anything", "anything", handler));

        Assert.Contains("incomplete two-factor challenge", exception.Message);
    }

    [Fact]
    public async Task ReportsCredentialRejectionWithoutReturningResponseBody()
    {
        StubHandler handler = new(
            HttpStatusCode.Unauthorized,
            "{\"error\":{\"message\":\"Invalid Username/Email or Password\"}}");

        VrchatCredentialException exception = await Assert.ThrowsAsync<VrchatCredentialException>(() =>
            VrchatCredentialValidator.ValidateAsync("wrong", "wrong", handler));

        Assert.Contains("Invalid Username", exception.Message);
    }

    private sealed class StubHandler(
        HttpStatusCode status,
        string responseBody,
        string? setCookie = null) : HttpMessageHandler
    {
        public string? Authorization { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Authorization = request.Headers.Authorization?.ToString();
            HttpResponseMessage response = new(status)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json")
            };
            if (setCookie != null) response.Headers.TryAddWithoutValidation("Set-Cookie", setCookie);
            return Task.FromResult(response);
        }
    }
}
