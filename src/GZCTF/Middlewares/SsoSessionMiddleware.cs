using System.Security.Claims;
using GZCTF.Models.Internal;
using GZCTF.Services.Sso;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace GZCTF.Middlewares;

/// <summary>
/// 在授权前检查 Keycloak back-channel logout 写入的撤销记录。
/// </summary>
internal sealed class SsoSessionMiddleware(RequestDelegate next, ILogger<SsoSessionMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context, IOptionsSnapshot<SsoConfig> options,
        SsoSessionStore sessionStore)
    {
        if (!options.Value.Enabled || context.User.Identity?.IsAuthenticated is not true)
        {
            await next(context);
            return;
        }

        var sub = context.User.FindFirstValue(SsoConstants.SubClaim);
        var loginAtValue = context.User.FindFirstValue(SsoConstants.LoginAtClaim);
        if (string.IsNullOrWhiteSpace(sub) || !long.TryParse(loginAtValue, out var loginAt))
        {
            await next(context);
            return;
        }

        try
        {
            var sid = context.User.FindFirstValue(SsoConstants.SidClaim);
            if (await sessionStore.IsRevokedAsync(sid, sub, loginAt, context.RequestAborted))
            {
                await context.SignOutAsync(IdentityConstants.ApplicationScheme);
                context.User = new ClaimsPrincipal(new ClaimsIdentity());
            }
        }
        catch
        {
            logger.LogError("无法检查 SSO 会话撤销状态");
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }

        await next(context);
    }
}
