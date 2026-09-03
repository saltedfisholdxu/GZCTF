using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace GZCTF.Services.Sso;

internal sealed record ValidatedLogoutToken(string Jti, string? Sid, string? Sub, long IssuedAt);

public sealed class SsoLogoutTokenValidator(IOptionsMonitor<OpenIdConnectOptions> oidcOptions)
{
    internal async Task<ValidatedLogoutToken?> ValidateAsync(string logoutToken, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(logoutToken))
            return null;

        var options = oidcOptions.Get(SsoConstants.Scheme);
        if (options.ConfigurationManager is null)
            return null;

        try
        {
            var configuration = await options.ConfigurationManager.GetConfigurationAsync(token);
            var parameters = options.TokenValidationParameters.Clone();
            parameters.ValidIssuer = configuration.Issuer;
            parameters.ValidAudience = options.ClientId;
            parameters.IssuerSigningKeys = configuration.SigningKeys;
            parameters.ValidateIssuer = true;
            parameters.ValidateAudience = true;
            parameters.ValidateIssuerSigningKey = true;
            parameters.RequireSignedTokens = true;
            parameters.RequireExpirationTime = false;
            parameters.ValidateLifetime = true;
            parameters.ClockSkew = SsoConstants.LogoutTokenClockSkew;

            var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
            var principal = handler.ValidateToken(logoutToken, parameters, out var validatedToken);
            if (validatedToken is not JwtSecurityToken jwt)
                return null;

            var jti = principal.FindFirstValue(JwtRegisteredClaimNames.Jti);
            var sid = principal.FindFirstValue("sid");
            var sub = principal.FindFirstValue(JwtRegisteredClaimNames.Sub);
            if (string.IsNullOrWhiteSpace(jti) ||
                string.IsNullOrWhiteSpace(sid) && string.IsNullOrWhiteSpace(sub) ||
                principal.HasClaim(claim => claim.Type == "nonce"))
                return null;

            if (!long.TryParse(principal.FindFirstValue(JwtRegisteredClaimNames.Iat),
                    NumberStyles.Integer, CultureInfo.InvariantCulture, out var issuedAtSeconds))
                return null;

            var issuedAt = DateTimeOffset.FromUnixTimeSeconds(issuedAtSeconds);
            if ((DateTimeOffset.UtcNow - issuedAt).Duration() > SsoConstants.LogoutTokenClockSkew)
                return null;

            if (!HasBackchannelLogoutEvent(jwt.Payload.SerializeToJson()))
                return null;

            return new(jti, sid, sub, issuedAtSeconds);
        }
        catch (Exception exception) when (exception is SecurityTokenException or JsonException or ArgumentException)
        {
            return null;
        }
    }

    internal static bool HasBackchannelLogoutEvent(string payload)
    {
        using var document = JsonDocument.Parse(payload);
        return document.RootElement.ValueKind == JsonValueKind.Object &&
               document.RootElement.TryGetProperty("events", out var events) &&
               events.ValueKind == JsonValueKind.Object &&
               events.TryGetProperty(SsoConstants.BackchannelLogoutEvent, out var logoutEvent) &&
               logoutEvent.ValueKind == JsonValueKind.Object;
    }
}
