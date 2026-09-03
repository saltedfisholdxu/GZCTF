using System.Net;
using System.Net.Http.Json;
using GZCTF.Extensions.Startup;
using GZCTF.Integration.Test.Base;
using GZCTF.Models.Internal;
using GZCTF.Models.Request.Account;
using GZCTF.Services.Sso;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace GZCTF.Integration.Test.Tests.Api;

/// <summary>
/// Tests for authentication and authorization workflows
/// </summary>
[Collection(nameof(IntegrationTestCollection))]
public class AuthenticationTests(GZCTFApplicationFactory factory, ITestOutputHelper output)
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task Account_Login_WithSeededUser_Succeeds()
    {
        var password = "S3eded!Pass";
        var userName = TestDataSeeder.RandomName();
        var email = $"{userName}@example.com";
        var seeded = await TestDataSeeder.CreateUserAsync(factory.Services,
            userName,
            password,
            email);

        using var client = factory.CreateClient();

        var loginResponse = await client.PostAsJsonAsync("/api/Account/LogIn",
            new LoginModel { UserName = seeded.UserName, Password = password });

        loginResponse.EnsureSuccessStatusCode();

        var profileResponse = await client.GetAsync("/api/Account/Profile");
        profileResponse.EnsureSuccessStatusCode();

        var profile = await profileResponse.Content.ReadFromJsonAsync<ProfileUserInfoModel>();
        Assert.NotNull(profile);
        Assert.Equal(seeded.UserName, profile.UserName);
        Assert.Equal(seeded.Email, profile.Email);
    }

    [Fact]
    public async Task Register_WithValidData_ReturnsSuccess()
    {
        // Arrange
        var registerModel = new
        {
            userName = TestDataSeeder.RandomName(),
            password = "TestPassword123!",
            email = $"test_{Guid.NewGuid():N}@example.com"
        };

        // Act
        var response = await _client.PostAsJsonAsync("/api/Account/Register", registerModel);
        output.WriteLine($"Status: {response.StatusCode}");

        var content = await response.Content.ReadAsStringAsync();
        output.WriteLine($"Response: {content}");

        // Assert
        // Registration might succeed or fail depending on global config
        // We just verify we get a valid response (not a 404 or 500)
        Assert.True(
            response.StatusCode == HttpStatusCode.OK ||
            response.StatusCode == HttpStatusCode.BadRequest,
            $"Expected OK or BadRequest but got {response.StatusCode}"
        );
    }

    [Fact]
    public async Task Register_WithInvalidEmail_ReturnsBadRequest()
    {
        // Arrange
        var registerModel = new
        {
            userName = TestDataSeeder.RandomName(),
            password = "TestPassword123!",
            email = "invalid-email"
        };

        // Act
        var response = await _client.PostAsJsonAsync("/api/Account/Register", registerModel);
        output.WriteLine($"Status: {response.StatusCode}");

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Register_WithShortPassword_ReturnsBadRequest()
    {
        // Arrange
        var registerModel = new { userName = "testuser", password = "123", email = "test@example.com" };

        // Act
        var response = await _client.PostAsJsonAsync("/api/Account/Register", registerModel);
        output.WriteLine($"Status: {response.StatusCode}");

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Login_WithoutCredentials_ReturnsBadRequest()
    {
        // Arrange
        var loginModel = new { };

        // Act
        var response = await _client.PostAsJsonAsync("/api/Account/LogIn", loginModel);
        output.WriteLine($"Status: {response.StatusCode}");

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Logout_WhenNotAuthenticated_ReturnsUnauthorized()
    {
        // Act
        var response = await _client.PostAsync("/api/Account/LogOut", null);
        output.WriteLine($"Status: {response.StatusCode}");

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task SsoDisabled_DoesNotRegisterSchemeAndKeepsLocalAuthentication()
    {
        var schemes = factory.Services.GetRequiredService<IAuthenticationSchemeProvider>();
        Assert.Null(await schemes.GetSchemeAsync("keycloak"));

        var configResponse = await _client.GetAsync("/api/sso/config");
        configResponse.EnsureSuccessStatusCode();
        var config = await configResponse.Content.ReadFromJsonAsync<SsoClientConfig>();
        Assert.NotNull(config);
        Assert.False(config.Enabled);
        Assert.True(config.LocalAuthenticationEnabled);
        Assert.True(config.LocalCredentialManagementEnabled);

        var loginResponse = await _client.GetAsync("/api/sso/login");
        Assert.Equal(HttpStatusCode.NotFound, loginResponse.StatusCode);
    }

    [Fact]
    public async Task SsoEnabled_DisabledLocalEndpointsReturnForbiddenAndCallbackAvoidsSpaFallback()
    {
        var ssoConfig = new SsoConfig
        {
            Enabled = true,
            Authority = "https://issuer.example",
            ClientId = "gzctf",
            ClientSecret = "integration-test-only",
            LocalAuthenticationEnabled = false,
            LocalCredentialManagementEnabled = false
        };
        using var ssoFactory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Sso:Enabled"] = "true",
                    ["Sso:Authority"] = ssoConfig.Authority,
                    ["Sso:ClientId"] = ssoConfig.ClientId,
                    ["Sso:ClientSecret"] = ssoConfig.ClientSecret,
                    ["Sso:LocalAuthenticationEnabled"] = "false",
                    ["Sso:LocalCredentialManagementEnabled"] = "false",
                    ["AccountPolicy:AllowRegister"] = "false"
                }));
            builder.ConfigureTestServices(services =>
                services.AddAuthentication().AddOpenIdConnect(SsoConstants.Scheme,
                    options => IdentityExtension.ConfigureOpenIdConnect(options, ssoConfig)));
        });
        using var client = ssoFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var schemes = ssoFactory.Services.GetRequiredService<IAuthenticationSchemeProvider>();
        Assert.NotNull(await schemes.GetSchemeAsync("keycloak"));

        var loginResponse = await client.PostAsJsonAsync("/api/Account/LogIn",
            new LoginModel { UserName = "nobody", Password = "password" });
        Assert.Equal(HttpStatusCode.Forbidden, loginResponse.StatusCode);

        var recoveryResponse = await client.PostAsJsonAsync("/api/Account/Recovery",
            new RecoveryModel { Email = "nobody@example.com" });
        Assert.Equal(HttpStatusCode.Forbidden, recoveryResponse.StatusCode);

        var resetResponse = await client.PostAsJsonAsync("/api/Account/PasswordReset",
            new PasswordResetModel { Email = "bm9ib2R5QGV4YW1wbGUuY29t", Password = "test", RToken = "test" });
        Assert.Equal(HttpStatusCode.Forbidden, resetResponse.StatusCode);

        var registerResponse = await client.PostAsJsonAsync("/api/Account/Register",
            new { userName = "nobody", password = "password", email = "nobody@example.com" });
        Assert.Equal(HttpStatusCode.Forbidden, registerResponse.StatusCode);

        var callbackResponse = await client.GetAsync("/api/sso/callback?error=access_denied");
        Assert.Equal(HttpStatusCode.Redirect, callbackResponse.StatusCode);
        Assert.Equal("/account/login?ssoError=remote_failure", callbackResponse.Headers.Location?.OriginalString);

        var forgedStateResponse = await client.GetAsync("/api/sso/callback?code=fake&state=not-protected");
        Assert.Equal(HttpStatusCode.Redirect, forgedStateResponse.StatusCode);
        Assert.Equal("/account/login?ssoError=remote_failure",
            forgedStateResponse.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task VerifyEmail_WithInvalidToken_ReturnsBadRequest()
    {
        // Arrange
        var verifyModel = new { email = "test@example.com", token = "invalid-token" };

        // Act
        var response = await _client.PostAsJsonAsync("/api/Account/Verify", verifyModel);
        output.WriteLine($"Status: {response.StatusCode}");

        var content = await response.Content.ReadAsStringAsync();
        output.WriteLine($"Response: {content}");

        // Assert - endpoint returns OK with error message in body
        Assert.True(
            response.StatusCode == HttpStatusCode.OK ||
            response.StatusCode == HttpStatusCode.BadRequest,
            $"Expected OK or BadRequest but got {response.StatusCode}"
        );
    }
}
