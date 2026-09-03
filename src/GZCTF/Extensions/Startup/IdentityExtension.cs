using GZCTF.Models.Internal;
using GZCTF.Services.Sso;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace GZCTF.Extensions.Startup;

internal static class IdentityExtension
{
    extension(WebApplicationBuilder builder)
    {
        public void ConfigureIdentity()
        {
            builder.Services.AddDataProtection().PersistKeysToDbContext<AppDbContext>();

            var ssoConfig = builder.Configuration.GetSection(SsoConfig.SectionName).Get<SsoConfig>() ?? new();
            ssoConfig.Validate();
            builder.Services.Configure<SsoConfig>(builder.Configuration.GetSection(SsoConfig.SectionName));

            var authentication = builder.Services.AddAuthentication(o =>
                {
                    o.DefaultScheme = IdentityConstants.ApplicationScheme;
                    o.DefaultSignInScheme = IdentityConstants.ExternalScheme;
                });

            authentication.AddIdentityCookies(options =>
                {
                    options.ApplicationCookie?.Configure(auth =>
                    {
                        auth.Cookie.Name = "GZCTF_Token";
                        auth.SlidingExpiration = true;
                        auth.ExpireTimeSpan = TimeSpan.FromDays(7);
                    });
                });

            if (ssoConfig.Enabled)
                authentication.AddOpenIdConnect(SsoConstants.Scheme,
                    options => ConfigureOpenIdConnect(options, ssoConfig));

            builder.Services.AddIdentityCore<UserInfo>(options =>
                {
                    options.User.RequireUniqueEmail = true;
                    options.Password.RequireNonAlphanumeric = false;
                    options.SignIn.RequireConfirmedEmail = true;

                    // Allow all characters in username
                    options.User.AllowedUserNameCharacters = string.Empty;
                })
                .AddSignInManager<SignInManager<UserInfo>>()
                .AddUserManager<UserManager<UserInfo>>()
                .AddEntityFrameworkStores<AppDbContext>()
                .AddErrorDescriber<TranslatedIdentityErrorDescriber>()
                .AddDefaultTokenProviders();

            builder.Services.Configure<DataProtectionTokenProviderOptions>(o =>
                o.TokenLifespan = TimeSpan.FromHours(3)
            );
        }
    }

    internal static void ConfigureOpenIdConnect(OpenIdConnectOptions options, SsoConfig ssoConfig)
    {
        options.Authority = ssoConfig.Authority.TrimEnd('/');
        options.ClientId = ssoConfig.ClientId;
        options.ClientSecret = ssoConfig.ClientSecret;
        options.SignInScheme = IdentityConstants.ExternalScheme;
        options.CallbackPath = SsoConstants.CallbackPath;
        options.SignedOutCallbackPath = SsoConstants.SignedOutPath;
        options.SignedOutRedirectUri = "/";
        options.ResponseType = OpenIdConnectResponseType.Code;
        options.ResponseMode = OpenIdConnectResponseMode.FormPost;
        // .NET 10 会在发现文档声明 PAR 端点时默认启用 PAR。当前 Keycloak 26.7.3
        // 在生产链路中未将 PAR 请求里的 state 带回 form_post 回调，严格 state 校验会拒绝登录。
        // 禁用 PAR 只改变授权参数的传递方式，不影响 Authorization Code、PKCE、nonce 或 state 校验。
        options.PushedAuthorizationBehavior = PushedAuthorizationBehavior.Disable;
        options.UsePkce = true;
        options.SaveTokens = true;
        options.GetClaimsFromUserInfoEndpoint = true;
        options.MapInboundClaims = false;
        options.RequireHttpsMetadata = true;
        options.Scope.Clear();
        options.Scope.Add(OpenIdConnectScope.OpenId);
        options.Scope.Add(OpenIdConnectScope.Profile);
        options.Scope.Add(OpenIdConnectScope.Email);
        options.ClaimActions.MapUniqueJsonKey("gzctf_uid", "gzctf_uid");
        options.ClaimActions.MapUniqueJsonKey("sid", "sid");
        options.ProtocolValidator.RequireNonce = true;
        options.ProtocolValidator.RequireState = true;
        options.ProtocolValidator.RequireStateValidation = true;
        options.ProtocolValidator.RequireSub = true;
        options.TokenValidationParameters.ValidateIssuer = true;
        options.TokenValidationParameters.ValidateAudience = true;
        options.TokenValidationParameters.ValidAudience = ssoConfig.ClientId;
        options.TokenValidationParameters.ValidateIssuerSigningKey = true;
        options.TokenValidationParameters.ValidateLifetime = true;
        options.TokenValidationParameters.RequireSignedTokens = true;
        options.TokenValidationParameters.RequireExpirationTime = true;

        options.Events = new OpenIdConnectEvents
        {
            OnRedirectToIdentityProviderForSignOut = context =>
            {
                var idToken = context.Properties.GetTokenValue(OpenIdConnectParameterNames.IdToken);
                if (!string.IsNullOrWhiteSpace(idToken))
                    context.ProtocolMessage.IdTokenHint = idToken;

                return Task.CompletedTask;
            },
            OnRemoteFailure = context =>
            {
                var logger = context.HttpContext.RequestServices
                    .GetRequiredService<ILoggerFactory>()
                    .CreateLogger("GZCTF.Sso");
                logger.LogWarning("SSO 远端认证失败，类型为 {FailureType}",
                    context.Failure?.GetType().Name ?? "Unknown");
                context.HandleResponse();
                context.Response.Redirect("/account/login?ssoError=remote_failure");
                return Task.CompletedTask;
            }
        };
    }
}
