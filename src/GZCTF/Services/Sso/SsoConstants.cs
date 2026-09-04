using System.Security.Cryptography;
using System.Text;

namespace GZCTF.Services.Sso;

internal static class SsoConstants
{
    public const string Scheme = "keycloak";
    public const string ProviderDisplayName = "Sf SSO";
    public const string DisplayNameClaim = "display_name";
    public const string ExternalLoginProviderItem = "LoginProvider";
    public const string CallbackPath = "/api/sso/callback";
    public const string CompletePath = "/api/sso/complete";
    public const string SignedOutPath = "/api/sso/signed-out";
    public const string SubClaim = "sftian:sub";
    public const string SidClaim = "sftian:sid";
    public const string LoginAtClaim = "sftian:login_at";
    public const string BackchannelLogoutEvent = "http://schemas.openid.net/event/backchannel-logout";
    public static readonly TimeSpan SessionRevocationLifetime = TimeSpan.FromDays(7);
    public static readonly TimeSpan LogoutTokenClockSkew = TimeSpan.FromMinutes(5);

    public static string SidRevocationKey(string sid) => $"sso:revoked:sid:{Hash(sid)}";

    public static string SubRevocationKey(string sub) => $"sso:revoked:sub:{Hash(sub)}";

    public static string LogoutTokenReplayKey(string jti) => $"sso:logout:jti:{Hash(jti)}";

    public static string StableSuffix(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexStringLower(hash.AsSpan(0, 4));
    }

    private static string Hash(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexStringLower(hash);
    }
}
