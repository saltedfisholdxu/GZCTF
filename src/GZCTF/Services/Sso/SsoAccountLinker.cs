using System.Security.Claims;
using GZCTF.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace GZCTF.Services.Sso;

internal enum SsoLinkFailure
{
    None,
    InvalidClaims,
    Banned,
    AmbiguousEmail,
    AlreadyLinked,
    CreateFailed,
    BindFailed
}

internal sealed record SsoLinkResult(UserInfo? User, SsoLinkFailure Failure, bool Created = false)
{
    public bool Succeeded => User is not null && Failure == SsoLinkFailure.None;
}

public sealed class SsoAccountLinker(
    UserManager<UserInfo> userManager,
    AppDbContext dbContext,
    ILogger<SsoAccountLinker> logger)
{
    internal async Task<SsoLinkResult> LinkAsync(ClaimsPrincipal principal, HttpContext context,
        CancellationToken token)
    {
        var sub = principal.FindFirstValue("sub")?.Trim();
        if (string.IsNullOrWhiteSpace(sub))
        {
            logger.LogWarning("SSO 登录缺少可信的 sub");
            return new(null, SsoLinkFailure.InvalidClaims);
        }

        var user = await userManager.FindByLoginAsync(SsoConstants.Scheme, sub);
        if (user is not null)
            return CheckUser(user);

        if (Guid.TryParse(principal.FindFirstValue("gzctf_uid"), out var migratedUserId))
        {
            user = await userManager.FindByIdAsync(migratedUserId.ToString());
            if (user is not null)
                return await BindAsync(user, sub, "source_id", false);
        }

        // 已完成绑定和迁移身份只按稳定标识认人；邮箱仅参与兜底首次绑定与新建账号。
        var email = principal.FindFirstValue("email")?.Trim();
        var emailVerified = bool.TryParse(principal.FindFirstValue("email_verified"), out var verified) && verified;
        if (string.IsNullOrWhiteSpace(email) || !emailVerified)
        {
            logger.LogWarning("SSO 首次登录缺少已验证邮箱");
            return new(null, SsoLinkFailure.InvalidClaims);
        }

        var normalizedEmail = userManager.NormalizeEmail(email);
        if (string.IsNullOrWhiteSpace(normalizedEmail))
            return new(null, SsoLinkFailure.InvalidClaims);

        var emailMatches = await userManager.Users
            .Where(item => item.NormalizedEmail == normalizedEmail)
            .Take(2)
            .ToListAsync(token);

        if (emailMatches.Count > 1)
        {
            logger.LogError("SSO 邮箱唯一关联失败，检测到多个本地账号");
            return new(null, SsoLinkFailure.AmbiguousEmail);
        }

        if (emailMatches.Count == 1)
            return await BindAsync(emailMatches[0], sub, "email", false);

        // 展示名称只决定新用户的初始本地名称，不能参与前面的身份匹配或覆盖老用户资料。
        var displayName = principal.FindFirstValue(SsoConstants.DisplayNameClaim);
        var preferredUserName = string.IsNullOrWhiteSpace(displayName)
            ? principal.FindFirstValue("preferred_username")
            : displayName;
        var userName = await CreateUniqueUserNameAsync(preferredUserName, email, sub);
        user = new UserInfo
        {
            UserName = userName,
            Email = email,
            EmailConfirmed = true,
            Role = Role.User,
            RegisterTimeUtc = DateTimeOffset.UtcNow,
            LastSignedInUtc = DateTimeOffset.UtcNow
        };
        user.UpdateByHttpContext(context);

        IdentityResult createResult;
        try
        {
            createResult = await userManager.CreateAsync(user);
        }
        catch (DbUpdateException)
        {
            // PostgreSQL 唯一约束会在并发首次登录时由其中一个请求触发。
            dbContext.ChangeTracker.Clear();
            var concurrentResult = await ResolveConcurrentCreationAsync(sub, normalizedEmail, token);
            if (concurrentResult is not null)
                return concurrentResult;

            logger.LogError("SSO 创建本地用户发生数据库唯一约束冲突，但未找到并发创建的账号");
            return new(null, SsoLinkFailure.CreateFailed);
        }

        if (!createResult.Succeeded)
        {
            var concurrentResult = await ResolveConcurrentCreationAsync(sub, normalizedEmail, token);
            if (concurrentResult is not null)
                return concurrentResult;

            logger.LogError("SSO 创建本地用户失败，错误码 {Codes}",
                string.Join(',', createResult.Errors.Select(error => error.Code)));
            return new(null, SsoLinkFailure.CreateFailed);
        }

        return await BindAsync(user, sub, "new_user", true);
    }

    private SsoLinkResult CheckUser(UserInfo user)
    {
        if (user.Role == Role.Banned)
        {
            logger.LogWarning("封禁用户尝试通过 SSO 登录，本地用户 {UserId}", user.Id);
            return new(null, SsoLinkFailure.Banned);
        }

        return new(user, SsoLinkFailure.None);
    }

    private async Task<SsoLinkResult> BindAsync(UserInfo user, string sub, string method, bool created)
    {
        var checkedUser = CheckUser(user);
        if (!checkedUser.Succeeded)
            return checkedUser;

        var existingLogins = await userManager.GetLoginsAsync(user);
        if (existingLogins.Any(login => login.LoginProvider == SsoConstants.Scheme && login.ProviderKey != sub))
        {
            logger.LogWarning("拒绝将第二个 SSO 身份关联到本地用户 {UserId}", user.Id);
            return new(null, SsoLinkFailure.AlreadyLinked);
        }

        IdentityResult result;
        try
        {
            result = await userManager.AddLoginAsync(user,
                new UserLoginInfo(SsoConstants.Scheme, sub, SsoConstants.ProviderDisplayName));
        }
        catch (DbUpdateException)
        {
            dbContext.ChangeTracker.Clear();
            return await ResolveConcurrentBindingAsync(user, sub, created);
        }

        if (result.Succeeded)
        {
            // 防止两个不同 sub 并发绑定同一个本地用户；发现竞争时移除本次绑定并拒绝登录。
            var currentLogins = await userManager.GetLoginsAsync(user);
            if (currentLogins.Any(login =>
                    login.LoginProvider == SsoConstants.Scheme && login.ProviderKey != sub))
            {
                var removeResult = await userManager.RemoveLoginAsync(user, SsoConstants.Scheme, sub);
                if (!removeResult.Succeeded)
                    logger.LogError("清理 SSO 并发冲突绑定失败，本地用户 {UserId}", user.Id);
                logger.LogWarning("SSO 并发绑定冲突，本地用户 {UserId}", user.Id);
                return new(null, SsoLinkFailure.AlreadyLinked);
            }

            logger.LogInformation("SSO 账号关联成功，方式 {LinkMethod}，本地用户 {UserId}", method, user.Id);
            return new(user, SsoLinkFailure.None, created);
        }

        dbContext.ChangeTracker.Clear();
        return await ResolveConcurrentBindingAsync(user, sub, created, result.Errors);
    }

    private async Task<SsoLinkResult?> ResolveConcurrentCreationAsync(string sub, string normalizedEmail,
        CancellationToken token)
    {
        dbContext.ChangeTracker.Clear();

        var concurrent = await userManager.FindByLoginAsync(SsoConstants.Scheme, sub);
        if (concurrent is not null)
            return CheckUser(concurrent);

        var emailMatches = await userManager.Users
            .Where(item => item.NormalizedEmail == normalizedEmail)
            .Take(2)
            .ToListAsync(token);
        return emailMatches.Count == 1
            ? await BindAsync(emailMatches[0], sub, "concurrent_email", false)
            : null;
    }

    private async Task<SsoLinkResult> ResolveConcurrentBindingAsync(UserInfo user, string sub, bool created,
        IEnumerable<IdentityError>? errors = null)
    {
        var concurrent = await userManager.FindByLoginAsync(SsoConstants.Scheme, sub);
        if (concurrent is not null)
        {
            if (created && concurrent.Id != user.Id)
                await DeleteOrphanAsync(user);
            return CheckUser(concurrent);
        }

        if (created)
            await DeleteOrphanAsync(user);

        logger.LogError("SSO 账号绑定失败，错误码 {Codes}",
            errors is null ? "DatabaseConflict" : string.Join(',', errors.Select(error => error.Code)));
        return new(null, SsoLinkFailure.BindFailed);
    }

    private async Task DeleteOrphanAsync(UserInfo user)
    {
        var current = await userManager.FindByIdAsync(user.Id.ToString());
        if (current is null)
            return;

        // 另一个请求可能已经把该用户成功绑定；此时绝不能删除它。
        if ((await userManager.GetLoginsAsync(current)).Count > 0)
            return;

        var result = await userManager.DeleteAsync(current);
        if (!result.Succeeded)
            logger.LogError("清理 SSO 并发产生的未绑定用户失败，本地用户 {UserId}", user.Id);
    }

    private async Task<string> CreateUniqueUserNameAsync(string? preferredUserName, string email, string sub)
    {
        var baseName = preferredUserName?.Trim();
        if (string.IsNullOrWhiteSpace(baseName))
            baseName = email.Split('@', 2)[0].Trim();
        if (string.IsNullOrWhiteSpace(baseName))
            baseName = "sftian";

        if (baseName.Length < Limits.MinUserNameLength)
            baseName = $"sftian-{SsoConstants.StableSuffix(sub)}";

        baseName = Truncate(baseName, Limits.MaxUserNameLength);
        if (await userManager.FindByNameAsync(baseName) is null)
            return baseName;

        var suffixLength = SsoConstants.StableSuffix(sub).Length;
        var prefix = Truncate(baseName, Limits.MaxUserNameLength - suffixLength - 1);
        for (var attempt = 0; attempt < 16; attempt++)
        {
            var suffix = SsoConstants.StableSuffix($"{sub}:{attempt}");
            var candidate = $"{prefix}-{suffix}";
            if (await userManager.FindByNameAsync(candidate) is null)
                return candidate;
        }

        throw new InvalidOperationException("无法为 SSO 用户生成唯一用户名");
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];
}
