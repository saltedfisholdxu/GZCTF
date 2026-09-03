using Microsoft.Extensions.Caching.Distributed;

namespace GZCTF.Services.Sso;

public sealed class SsoSessionStore(IDistributedCache cache)
{
    private static readonly DistributedCacheEntryOptions SessionOptions = new()
    {
        AbsoluteExpirationRelativeToNow = SsoConstants.SessionRevocationLifetime
    };

    private static readonly DistributedCacheEntryOptions ReplayOptions = new()
    {
        AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10)
    };

    public async Task<bool> IsRevokedAsync(string? sid, string sub, long loginAt, CancellationToken token)
    {
        if (!string.IsNullOrWhiteSpace(sid) &&
            await cache.GetStringAsync(SsoConstants.SidRevocationKey(sid), token) is not null)
            return true;

        var revokedAtValue = await cache.GetStringAsync(SsoConstants.SubRevocationKey(sub), token);
        return long.TryParse(revokedAtValue, out var revokedAt) && loginAt <= revokedAt;
    }

    public Task<bool> WasProcessedAsync(string jti, CancellationToken token) =>
        IsPresentAsync(SsoConstants.LogoutTokenReplayKey(jti), token);

    internal async Task RevokeAsync(ValidatedLogoutToken logoutToken, CancellationToken token)
    {
        if (!string.IsNullOrWhiteSpace(logoutToken.Sid))
            await cache.SetStringAsync(SsoConstants.SidRevocationKey(logoutToken.Sid), "1", SessionOptions, token);
        else if (!string.IsNullOrWhiteSpace(logoutToken.Sub))
            await cache.SetStringAsync(SsoConstants.SubRevocationKey(logoutToken.Sub),
                logoutToken.IssuedAt.ToString(), SessionOptions, token);

        await cache.SetStringAsync(SsoConstants.LogoutTokenReplayKey(logoutToken.Jti), "1", ReplayOptions, token);
    }

    private async Task<bool> IsPresentAsync(string key, CancellationToken token) =>
        await cache.GetStringAsync(key, token) is not null;
}
