using System.Security.Claims;
using GZCTF.Models.Internal;
using GZCTF.Services.Sso;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace GZCTF.Controllers;

/// <summary>
/// SfTian OIDC 登录与登出接口。
/// </summary>
[ApiController]
[Route("api/sso")]
public sealed class SsoController(
    UserManager<UserInfo> userManager,
    SignInManager<UserInfo> signInManager,
    SsoAccountLinker accountLinker,
    SsoLogoutTokenValidator logoutTokenValidator,
    SsoSessionStore sessionStore,
    IOptionsSnapshot<SsoConfig> ssoOptions,
    IOptionsSnapshot<AccountPolicy> accountOptions,
    IConfiguration configuration,
    ILogger<SsoController> logger) : ControllerBase
{
    /// <summary>
    /// 获取不含密钥的 SSO 客户端配置。
    /// </summary>
    [HttpGet("config")]
    [ProducesResponseType(typeof(SsoClientConfig), StatusCodes.Status200OK)]
    public IActionResult Config()
    {
        var options = ssoOptions.Value;
        var hasDistributedCache = !string.IsNullOrWhiteSpace(configuration.GetConnectionString("RedisCache"));
        return Ok(new SsoClientConfig(
            options.Enabled,
            !options.Enabled || options.LocalAuthenticationEnabled,
            !options.Enabled || options.LocalCredentialManagementEnabled,
            !options.Enabled || accountOptions.Value.AllowRegister,
            options.Enabled && hasDistributedCache,
            options.Enabled ? options.Authority : null,
            options.Enabled ? options.ClientId : null));
    }

    /// <summary>
    /// 发起 SfTian OIDC 登录。
    /// </summary>
    [HttpGet("login")]
    public IActionResult Login([FromQuery] string? returnUrl = null)
    {
        if (!ssoOptions.Value.Enabled)
            return NotFound();

        var safeReturnUrl = SafeReturnUrl(Url, returnUrl);
        var completeUrl = Url.Action(nameof(Complete), values: new { returnUrl = safeReturnUrl })!;
        var properties = signInManager.ConfigureExternalAuthenticationProperties(SsoConstants.Scheme, completeUrl);
        return Challenge(properties, SsoConstants.Scheme);
    }

    /// <summary>
    /// 将通过验证的外部身份关联到本地账号并签发正式 Cookie。
    /// </summary>
    [HttpGet("complete")]
    public async Task<IActionResult> Complete([FromQuery] string? returnUrl = null,
        CancellationToken token = default)
    {
        if (!ssoOptions.Value.Enabled)
            return NotFound();

        var external = await HttpContext.AuthenticateAsync(IdentityConstants.ExternalScheme);
        if (!external.Succeeded || external.Principal is null || external.Properties is null ||
            !external.Properties.Items.TryGetValue(SsoConstants.ExternalLoginProviderItem, out var loginProvider) ||
            loginProvider != SsoConstants.Scheme)
            return await FailAsync("external_cookie_invalid");

        var idToken = external.Properties.GetTokenValue(OpenIdConnectParameterNames.IdToken);
        if (string.IsNullOrWhiteSpace(idToken))
            return await FailAsync("external_cookie_invalid");

        var link = await accountLinker.LinkAsync(external.Principal, HttpContext, token);
        if (!link.Succeeded)
            return await FailAsync(link.Failure switch
            {
                SsoLinkFailure.Banned => "banned",
                SsoLinkFailure.AmbiguousEmail => "ambiguous_email",
                SsoLinkFailure.AlreadyLinked => "already_linked",
                SsoLinkFailure.InvalidClaims => "invalid_claims",
                _ => "link_failed"
            });

        var user = link.User!;
        user.LastSignedInUtc = DateTimeOffset.UtcNow;
        user.UpdateByHttpContext(HttpContext);
        var updateResult = await userManager.UpdateAsync(user);
        if (!updateResult.Succeeded)
        {
            logger.LogError("SSO 登录更新本地用户失败，用户 {UserId}，错误码 {Codes}", user.Id,
                string.Join(',', updateResult.Errors.Select(error => error.Code)));
            return await FailAsync("local_signin_failed");
        }

        var sub = external.Principal.FindFirstValue("sub")!;
        var sid = external.Principal.FindFirstValue("sid");
        var loginAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var additionalClaims = new List<Claim>
        {
            new(SsoConstants.SubClaim, sub),
            new(SsoConstants.LoginAtClaim, loginAt)
        };
        if (!string.IsNullOrWhiteSpace(sid))
            additionalClaims.Add(new(SsoConstants.SidClaim, sid));

        var applicationProperties = new AuthenticationProperties { IsPersistent = true };
        applicationProperties.StoreTokens([
            new AuthenticationToken { Name = OpenIdConnectParameterNames.IdToken, Value = idToken }
        ]);

        await signInManager.SignInWithClaimsAsync(user, applicationProperties, additionalClaims);
        await HttpContext.SignOutAsync(IdentityConstants.ExternalScheme);

        logger.LogInformation("SSO 登录成功，本地用户 {UserId}，是否新建 {Created}", user.Id, link.Created);
        return LocalRedirect(SafeReturnUrl(Url, returnUrl));
    }

    /// <summary>
    /// 清理本地会话并结束 Keycloak 会话。
    /// </summary>
    [HttpPost("logout")]
    public async Task<IActionResult> Logout()
    {
        if (!ssoOptions.Value.Enabled)
            return NotFound();

        var application = await HttpContext.AuthenticateAsync(IdentityConstants.ApplicationScheme);
        var idToken = application.Properties?.GetTokenValue(OpenIdConnectParameterNames.IdToken);
        if (string.IsNullOrWhiteSpace(idToken))
        {
            await HttpContext.SignOutAsync(IdentityConstants.ApplicationScheme);
            await HttpContext.SignOutAsync(IdentityConstants.ExternalScheme);
            return LocalRedirect("/");
        }

        var properties = new AuthenticationProperties { RedirectUri = "/" };
        properties.StoreTokens([
            new AuthenticationToken { Name = OpenIdConnectParameterNames.IdToken, Value = idToken }
        ]);
        return SignOut(properties,
            IdentityConstants.ApplicationScheme,
            IdentityConstants.ExternalScheme,
            SsoConstants.Scheme);
    }

    /// <summary>
    /// Keycloak 前通道登出后的固定落点。
    /// </summary>
    [HttpGet("signed-out")]
    public IActionResult SignedOut() => LocalRedirect("/");

    /// <summary>
    /// 接收并验证 Keycloak back-channel logout token。
    /// </summary>
    [HttpPost("backchannel-logout")]
    [Consumes("application/x-www-form-urlencoded")]
    public async Task<IActionResult> BackchannelLogout(
        [FromForm(Name = "logout_token")] string? logoutToken,
        CancellationToken token = default)
    {
        if (!ssoOptions.Value.Enabled)
            return NotFound();

        if (string.IsNullOrWhiteSpace(configuration.GetConnectionString("RedisCache")))
            return StatusCode(StatusCodes.Status503ServiceUnavailable);

        var validated = await logoutTokenValidator.ValidateAsync(logoutToken ?? string.Empty, token);
        if (validated is null)
        {
            logger.LogWarning("拒绝无效的 SSO back-channel logout token");
            return BadRequest();
        }

        try
        {
            if (!await sessionStore.WasProcessedAsync(validated.Jti, token))
                await sessionStore.RevokeAsync(validated, token);
        }
        catch
        {
            logger.LogError("写入 SSO 会话撤销记录失败");
            return StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        return Ok();
    }

    internal static string SafeReturnUrl(IUrlHelper urlHelper, string? returnUrl) =>
        !string.IsNullOrWhiteSpace(returnUrl) && urlHelper.IsLocalUrl(returnUrl) ? returnUrl : "/";

    private async Task<IActionResult> FailAsync(string code)
    {
        await HttpContext.SignOutAsync(IdentityConstants.ExternalScheme);
        return LocalRedirect($"/account/login?ssoError={Uri.EscapeDataString(code)}");
    }
}
