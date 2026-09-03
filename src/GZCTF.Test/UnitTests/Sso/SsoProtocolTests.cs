using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using GZCTF.Controllers;
using GZCTF.Extensions.Startup;
using GZCTF.Models.Internal;
using GZCTF.Services.Sso;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace GZCTF.Test.UnitTests.Sso;

public class SsoProtocolTests
{
    [Theory]
    [InlineData(null, "/")]
    [InlineData("", "/")]
    [InlineData("/", "/")]
    [InlineData("/games/1?tab=challenges", "/games/1?tab=challenges")]
    [InlineData("~/account/profile", "~/account/profile")]
    [InlineData("https://evil.example/", "/")]
    [InlineData("//evil.example/", "/")]
    [InlineData("/\\evil.example/", "/")]
    [InlineData("~//evil.example/", "/")]
    [InlineData("~/\\evil.example/", "/")]
    [InlineData("javascript:alert(1)", "/")]
    public void SafeReturnUrl_OnlyAllowsLocalPaths(string? value, string expected)
    {
        var urlHelper = new UrlHelper(new ActionContext(
            new DefaultHttpContext(), new RouteData(), new ActionDescriptor()));
        Assert.Equal(expected, SsoController.SafeReturnUrl(urlHelper, value));
    }

    [Fact]
    public void BackchannelEvent_RequiresExpectedEventObject()
    {
        const string valid =
            """{"iss":"https://issuer.example","events":{"http://schemas.openid.net/event/backchannel-logout":{}}}""";
        const string wrongEvent = """{"events":{"https://example.invalid/event":{}}}""";
        const string wrongShape =
            """{"events":{"http://schemas.openid.net/event/backchannel-logout":"logout"}}""";

        Assert.True(SsoLogoutTokenValidator.HasBackchannelLogoutEvent(valid));
        Assert.False(SsoLogoutTokenValidator.HasBackchannelLogoutEvent(wrongEvent));
        Assert.False(SsoLogoutTokenValidator.HasBackchannelLogoutEvent(wrongShape));
    }

    [Fact]
    public async Task SessionStore_RevokesSidAndOnlyOlderSubSessions()
    {
        IDistributedCache cache = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        var store = new SsoSessionStore(cache);

        await store.RevokeAsync(new("jti-sid", "sid-1", "sub-1", 100), CancellationToken.None);
        Assert.True(await store.IsRevokedAsync("sid-1", "sub-1", 200, CancellationToken.None));
        Assert.False(await store.IsRevokedAsync("sid-2", "sub-1", 50, CancellationToken.None));
        Assert.True(await store.WasProcessedAsync("jti-sid", CancellationToken.None));

        await store.RevokeAsync(new("jti-sub", null, "sub-2", 100), CancellationToken.None);
        Assert.True(await store.IsRevokedAsync(null, "sub-2", 100, CancellationToken.None));
        Assert.True(await store.IsRevokedAsync(null, "sub-2", 99, CancellationToken.None));
        Assert.False(await store.IsRevokedAsync(null, "sub-2", 101, CancellationToken.None));
    }

    [Fact]
    public void DisabledSso_DoesNotRequireOidcConfiguration()
    {
        var disabled = new SsoConfig();
        disabled.Validate();

        var insecure = new SsoConfig
        {
            Enabled = true,
            Authority = "http://issuer.example",
            ClientId = "gzctf",
            ClientSecret = "test-only"
        };
        Assert.Throws<InvalidOperationException>(insecure.Validate);
    }

    [Fact]
    public void OidcOptions_RequireAuthorizationCodePkceAndStrictTokenValidation()
    {
        var config = new SsoConfig
        {
            Enabled = true,
            Authority = "https://sso.example/realms/test/",
            ClientId = "gzctf",
            ClientSecret = "test-only"
        };
        var options = new OpenIdConnectOptions();

        IdentityExtension.ConfigureOpenIdConnect(options, config);

        Assert.Equal("https://sso.example/realms/test", options.Authority);
        Assert.Equal(IdentityConstants.ExternalScheme, options.SignInScheme);
        Assert.Equal(SsoConstants.CallbackPath, options.CallbackPath);
        Assert.Equal(OpenIdConnectResponseType.Code, options.ResponseType);
        Assert.Equal(OpenIdConnectResponseMode.FormPost, options.ResponseMode);
        Assert.True(options.UsePkce);
        Assert.True(options.SaveTokens);
        Assert.True(options.RequireHttpsMetadata);
        Assert.False(options.MapInboundClaims);
        Assert.Equal([OpenIdConnectScope.OpenId, OpenIdConnectScope.Profile, OpenIdConnectScope.Email],
            options.Scope);
        Assert.True(options.ProtocolValidator.RequireNonce);
        Assert.True(options.ProtocolValidator.RequireState);
        Assert.True(options.ProtocolValidator.RequireStateValidation);
        Assert.True(options.ProtocolValidator.RequireSub);
        Assert.True(options.TokenValidationParameters.ValidateIssuer);
        Assert.True(options.TokenValidationParameters.ValidateAudience);
        Assert.True(options.TokenValidationParameters.ValidateIssuerSigningKey);
        Assert.True(options.TokenValidationParameters.ValidateLifetime);
        Assert.True(options.TokenValidationParameters.RequireSignedTokens);
        Assert.True(options.TokenValidationParameters.RequireExpirationTime);
        Assert.Equal(config.ClientId, options.TokenValidationParameters.ValidAudience);
    }

    [Fact]
    public async Task LogoutTokenValidator_RejectsTokenConfusionAndInvalidRequiredClaims()
    {
        const string issuer = "https://issuer.example/realms/test";
        const string audience = "gzctf";
        using var trustedRsa = RSA.Create(2048);
        using var untrustedRsa = RSA.Create(2048);
        var trustedKey = new RsaSecurityKey(trustedRsa) { KeyId = "trusted" };
        var configuration = new OpenIdConnectConfiguration { Issuer = issuer };
        configuration.SigningKeys.Add(trustedKey);
        var options = new OpenIdConnectOptions
        {
            ClientId = audience,
            ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(configuration)
        };
        var validator = new SsoLogoutTokenValidator(new StaticOptionsMonitor<OpenIdConnectOptions>(options));
        var issuedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var valid = await validator.ValidateAsync(
            CreateLogoutToken(trustedKey, issuer, audience, issuedAt), CancellationToken.None);
        Assert.NotNull(valid);
        Assert.Equal("logout-jti", valid.Jti);
        Assert.Equal("session-id", valid.Sid);
        Assert.Equal("subject-id", valid.Sub);

        var invalidTokens = new[]
        {
            CreateLogoutToken(trustedKey, "https://other-issuer.example", audience, issuedAt),
            CreateLogoutToken(trustedKey, issuer, "other-client", issuedAt),
            CreateLogoutToken(new RsaSecurityKey(untrustedRsa) { KeyId = "untrusted" }, issuer, audience, issuedAt),
            CreateLogoutToken(trustedKey, issuer, audience, issuedAt, includeEvent: false),
            CreateLogoutToken(trustedKey, issuer, audience, issuedAt, includeNonce: true),
            CreateLogoutToken(trustedKey, issuer, audience, issuedAt, includeJti: false),
            CreateLogoutToken(trustedKey, issuer, audience, issuedAt, includeSession: false),
            CreateLogoutToken(trustedKey, issuer, audience, issuedAt - 600)
        };

        foreach (var invalidToken in invalidTokens)
            Assert.Null(await validator.ValidateAsync(invalidToken, CancellationToken.None));
    }

    private static string CreateLogoutToken(SecurityKey signingKey, string issuer, string audience, long issuedAt,
        bool includeEvent = true, bool includeNonce = false, bool includeJti = true, bool includeSession = true)
    {
        var payload = new JwtPayload
        {
            { JwtRegisteredClaimNames.Iss, issuer },
            { JwtRegisteredClaimNames.Aud, audience },
            { JwtRegisteredClaimNames.Iat, issuedAt },
            { JwtRegisteredClaimNames.Exp, issuedAt + 300 }
        };
        if (includeJti)
            payload.Add(JwtRegisteredClaimNames.Jti, "logout-jti");
        if (includeSession)
        {
            payload.Add("sid", "session-id");
            payload.Add(JwtRegisteredClaimNames.Sub, "subject-id");
        }
        if (includeEvent)
            payload.Add("events", new Dictionary<string, object>
            {
                [SsoConstants.BackchannelLogoutEvent] = new Dictionary<string, object>()
            });
        if (includeNonce)
            payload.Add("nonce", "not-allowed");

        var header = new JwtHeader(new SigningCredentials(signingKey, SecurityAlgorithms.RsaSha256));
        return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(header, payload));
    }

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;

        public T Get(string? name) => value;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
