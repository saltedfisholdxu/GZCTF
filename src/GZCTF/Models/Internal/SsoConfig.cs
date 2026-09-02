namespace GZCTF.Models.Internal;

/// <summary>
/// SfTian 单点登录配置，仅从部署配置读取。
/// </summary>
public sealed class SsoConfig
{
    public const string SectionName = "Sso";

    /// <summary>
    /// 是否启用 OIDC 登录。
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Keycloak realm 地址。
    /// </summary>
    public string Authority { get; set; } = string.Empty;

    /// <summary>
    /// OIDC client id。
    /// </summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>
    /// OIDC confidential client secret。
    /// </summary>
    public string ClientSecret { get; set; } = string.Empty;

    /// <summary>
    /// SSO 启用期间是否继续允许本地密码登录。
    /// </summary>
    public bool LocalAuthenticationEnabled { get; set; } = true;

    /// <summary>
    /// SSO 启用期间是否继续允许找回、重置和修改本地凭据。
    /// </summary>
    public bool LocalCredentialManagementEnabled { get; set; } = true;

    internal void Validate()
    {
        if (!Enabled)
            return;

        if (!Uri.TryCreate(Authority, UriKind.Absolute, out var authority) ||
            authority.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("启用 SSO 时必须配置 HTTPS Authority");

        if (string.IsNullOrWhiteSpace(ClientId) || string.IsNullOrWhiteSpace(ClientSecret))
            throw new InvalidOperationException("启用 SSO 时必须配置 ClientId 和 ClientSecret");
    }
}

/// <summary>
/// 可安全返回给前端的 SSO 状态。
/// </summary>
public sealed record SsoClientConfig(
    bool Enabled,
    bool LocalAuthenticationEnabled,
    bool LocalCredentialManagementEnabled,
    bool RegistrationEnabled,
    bool BackchannelLogoutEnabled,
    string? Authority,
    string? ClientId);
