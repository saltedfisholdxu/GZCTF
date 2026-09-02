using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using GZCTF.Models;
using GZCTF.Models.Data;
using GZCTF.Services.Sso;
using GZCTF.Utils;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GZCTF.Test.UnitTests.Sso;

public class SsoAccountLinkerTests
{
    [Fact]
    public async Task ExistingLogin_UsesSubWithoutRequiringEmailAgain()
    {
        await using var services = CreateServices();
        var user = await CreateUserAsync(services, "legacy", "legacy@example.com");
        await AddLoginAsync(services, user, "subject-1");

        using var scope = services.CreateScope();
        var result = await scope.ServiceProvider.GetRequiredService<SsoAccountLinker>()
            .LinkAsync(Principal("subject-1"), new DefaultHttpContext(), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(user.Id, result.User!.Id);
        Assert.False(result.Created);
    }

    [Fact]
    public async Task MigratedId_TakesPriorityOverEmailAndPreservesLocalData()
    {
        await using var services = CreateServices();
        var migrated = await CreateUserAsync(services, "migrated", "old@example.com", Role.Monitor);
        var emailOwner = await CreateUserAsync(services, "email-owner", "new@example.com");

        using var scope = services.CreateScope();
        var result = await scope.ServiceProvider.GetRequiredService<SsoAccountLinker>()
            .LinkAsync(Principal("subject-2", "new@example.com", migrated.Id),
                new DefaultHttpContext(), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(migrated.Id, result.User!.Id);
        Assert.Equal("migrated", result.User.UserName);
        Assert.Equal("old@example.com", result.User.Email);
        Assert.Equal(Role.Monitor, result.User.Role);
        Assert.NotEqual(emailOwner.Id, result.User.Id);
    }

    [Fact]
    public async Task UniqueVerifiedEmail_LinksExistingUserOnlyOnce()
    {
        await using var services = CreateServices();
        var existing = await CreateUserAsync(services, "email-match", "match@example.com");

        SsoLinkResult first;
        using (var scope = services.CreateScope())
        {
            first = await scope.ServiceProvider.GetRequiredService<SsoAccountLinker>()
                .LinkAsync(Principal("subject-3", " MATCH@example.com "),
                    new DefaultHttpContext(), CancellationToken.None);
        }

        SsoLinkResult second;
        using (var scope = services.CreateScope())
        {
            second = await scope.ServiceProvider.GetRequiredService<SsoAccountLinker>()
                .LinkAsync(Principal("subject-3"), new DefaultHttpContext(), CancellationToken.None);
        }

        Assert.True(first.Succeeded);
        Assert.True(second.Succeeded);
        Assert.Equal(existing.Id, first.User!.Id);
        Assert.Equal(existing.Id, second.User!.Id);

        using var verifyScope = services.CreateScope();
        var context = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(1, await context.Users.CountAsync());
        Assert.Single(await context.UserLogins.Where(login => login.UserId == existing.Id).ToListAsync());
    }

    [Fact]
    public async Task ConcurrentFirstLogin_ConvergesOnOneBinding()
    {
        await using var services = CreateServices();
        var existing = await CreateUserAsync(services, "concurrent", "concurrent@example.com");
        var principal = Principal("subject-concurrent", "concurrent@example.com", existing.Id);

        using var firstScope = services.CreateScope();
        using var secondScope = services.CreateScope();
        var firstLinker = firstScope.ServiceProvider.GetRequiredService<SsoAccountLinker>();
        var secondLinker = secondScope.ServiceProvider.GetRequiredService<SsoAccountLinker>();

        var results = await Task.WhenAll(
            firstLinker.LinkAsync(principal, new DefaultHttpContext(), CancellationToken.None),
            secondLinker.LinkAsync(principal, new DefaultHttpContext(), CancellationToken.None));

        Assert.All(results, result =>
        {
            Assert.True(result.Succeeded);
            Assert.Equal(existing.Id, result.User!.Id);
        });

        using var verifyScope = services.CreateScope();
        var context = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(1, await context.Users.CountAsync());
        Assert.Single(await context.UserLogins.ToListAsync());
    }

    [Fact]
    public async Task BannedUser_IsRejectedBeforeBinding()
    {
        await using var services = CreateServices();
        var banned = await CreateUserAsync(services, "banned", "banned@example.com", Role.Banned);

        using var scope = services.CreateScope();
        var result = await scope.ServiceProvider.GetRequiredService<SsoAccountLinker>()
            .LinkAsync(Principal("subject-banned", "banned@example.com", banned.Id),
                new DefaultHttpContext(), CancellationToken.None);

        Assert.Equal(SsoLinkFailure.Banned, result.Failure);
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<UserInfo>>();
        Assert.Empty(await userManager.GetLoginsAsync(banned));
    }

    [Fact]
    public async Task ExistingDifferentSubjectBinding_IsRejected()
    {
        await using var services = CreateServices();
        var user = await CreateUserAsync(services, "linked", "linked@example.com");
        await AddLoginAsync(services, user, "original-subject");

        using var scope = services.CreateScope();
        var result = await scope.ServiceProvider.GetRequiredService<SsoAccountLinker>()
            .LinkAsync(Principal("second-subject", "linked@example.com", user.Id),
                new DefaultHttpContext(), CancellationToken.None);

        Assert.Equal(SsoLinkFailure.AlreadyLinked, result.Failure);
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<UserInfo>>();
        var current = await userManager.FindByIdAsync(user.Id.ToString());
        Assert.NotNull(current);
        var logins = await userManager.GetLoginsAsync(current);
        Assert.Single(logins);
        Assert.Equal("original-subject", logins[0].ProviderKey);
    }

    [Fact]
    public async Task NewUser_IsConfirmedRegularUserAndExternalRolesAreIgnored()
    {
        await using var services = CreateServices();
        using var scope = services.CreateScope();
        var principal = Principal("subject-new", "new@example.com", preferredUserName: "new-user");
        ((ClaimsIdentity)principal.Identity!).AddClaim(new Claim("realm_access", "Admin"));

        var result = await scope.ServiceProvider.GetRequiredService<SsoAccountLinker>()
            .LinkAsync(principal, new DefaultHttpContext(), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.True(result.Created);
        Assert.True(result.User!.EmailConfirmed);
        Assert.Equal(Role.User, result.User.Role);
        Assert.Equal("new-user", result.User.UserName);
    }

    [Fact]
    public async Task FirstLogin_RequiresVerifiedEmail()
    {
        await using var services = CreateServices();
        using var scope = services.CreateScope();
        var result = await scope.ServiceProvider.GetRequiredService<SsoAccountLinker>()
            .LinkAsync(Principal("subject-unverified", "unverified@example.com", emailVerified: false),
                new DefaultHttpContext(), CancellationToken.None);

        Assert.Equal(SsoLinkFailure.InvalidClaims, result.Failure);
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Empty(await context.Users.ToListAsync());
    }

    [Fact]
    public async Task AmbiguousNormalizedEmail_IsRejected()
    {
        await using var services = CreateServices();
        using (var seedScope = services.CreateScope())
        {
            var context = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();
            context.Users.AddRange(
                RawUser("duplicate-a", "duplicate@example.com"),
                RawUser("duplicate-b", "DUPLICATE@example.com"));
            await context.SaveChangesAsync();
        }

        using var scope = services.CreateScope();
        var result = await scope.ServiceProvider.GetRequiredService<SsoAccountLinker>()
            .LinkAsync(Principal("subject-ambiguous", "duplicate@example.com"),
                new DefaultHttpContext(), CancellationToken.None);

        Assert.Equal(SsoLinkFailure.AmbiguousEmail, result.Failure);
    }

    private static ServiceProvider CreateServices()
    {
        var services = new ServiceCollection();
        var databaseName = $"sso-tests-{Guid.NewGuid():N}";
        services.AddLogging();
        services.AddDbContext<AppDbContext>(options =>
            options.UseInMemoryDatabase(databaseName));
        services.AddIdentityCore<UserInfo>(options =>
            {
                options.User.RequireUniqueEmail = true;
                options.User.AllowedUserNameCharacters = string.Empty;
            })
            .AddUserManager<UserManager<UserInfo>>()
            .AddEntityFrameworkStores<AppDbContext>();
        services.AddScoped<SsoAccountLinker>();
        return services.BuildServiceProvider();
    }

    private static async Task<UserInfo> CreateUserAsync(IServiceProvider services, string userName, string email,
        Role role = Role.User)
    {
        using var scope = services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<UserInfo>>();
        var user = new UserInfo
        {
            UserName = userName,
            Email = email,
            EmailConfirmed = true,
            Role = role
        };
        var result = await userManager.CreateAsync(user);
        Assert.True(result.Succeeded, string.Join(',', result.Errors.Select(error => error.Code)));
        return user;
    }

    private static async Task AddLoginAsync(IServiceProvider services, UserInfo user, string sub)
    {
        using var scope = services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<UserInfo>>();
        var current = await userManager.FindByIdAsync(user.Id.ToString());
        var result = await userManager.AddLoginAsync(current!,
            new UserLoginInfo(SsoConstants.Scheme, sub, SsoConstants.ProviderDisplayName));
        Assert.True(result.Succeeded, string.Join(',', result.Errors.Select(error => error.Code)));
    }

    private static ClaimsPrincipal Principal(string sub, string? email = null, Guid? migratedId = null,
        string? preferredUserName = null, bool emailVerified = true)
    {
        var claims = new List<Claim> { new("sub", sub) };
        if (email is not null)
        {
            claims.Add(new("email", email));
            claims.Add(new("email_verified", emailVerified.ToString().ToLowerInvariant()));
        }

        if (migratedId is not null)
            claims.Add(new("gzctf_uid", migratedId.Value.ToString()));
        if (preferredUserName is not null)
            claims.Add(new("preferred_username", preferredUserName));

        return new ClaimsPrincipal(new ClaimsIdentity(claims, SsoConstants.Scheme));
    }

    private static UserInfo RawUser(string userName, string email) => new()
    {
        UserName = userName,
        NormalizedUserName = userName.ToUpperInvariant(),
        Email = email,
        NormalizedEmail = email.ToUpperInvariant(),
        EmailConfirmed = true,
        Role = Role.User,
        SecurityStamp = Guid.NewGuid().ToString()
    };
}
