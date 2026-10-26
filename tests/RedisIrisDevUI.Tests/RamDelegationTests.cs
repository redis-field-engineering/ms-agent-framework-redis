using System.Security.Claims;
using Microsoft.Agents.AI;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Redis.Iris.ContextRetriever;
using Xunit;

namespace RedisIrisDevUI.Tests;

public sealed class RamDelegationTests
{
    [Fact]
    public void SubjectOwnershipIsOptInAndSeparatesSubjectsWithTheSameTravelBoundary()
    {
        var first = User("user-1");
        var second = User("user-2");
        Assert.Equal("6-tenant-8-traveler", first.RamOwnerId);
        first = first with { UseSubjectForRamOwner = true };
        second = second with { UseSubjectForRamOwner = true };
        Assert.Equal("user-1", first.RamOwnerId);
        Assert.Equal("user-2", second.RamOwnerId);
        Assert.NotEqual(first.IsolationKey, second.IsolationKey);
    }

    [Theory]
    [InlineData("")]
    [InlineData("user@example.com")]
    [InlineData("user_1")]
    [InlineData("üser")]
    public void IncompatibleSubjectCannotBeMappedLossily(string subject)
    {
        Assert.Throws<UnauthorizedAccessException>(() => (User(subject) with { UseSubjectForRamOwner = true }).RamOwnerId);
    }

    [Fact]
    public void DelegationCannotBeEnabledWithoutOidc()
    {
        Assert.Throws<InvalidOperationException>(() => new TravelUserAccessor(
            new HttpContextAccessor(), TravelUserDirectory.Parse(null), User("legacy"), TravelAuthenticationMode.Token, true));
    }

    [Fact]
    public async Task RamUsesTheAuthenticatedTicketAndValidatedSubject()
    {
        var principal = Principal("user-1");
        using var services = Services(principal);
        var users = Accessor(principal, services);

        var token = await new TravelRamDelegatedTokenProvider(users).GetTokenAsync();

        Assert.Equal("signed-access-token", token.Token);
        Assert.Equal("user-1", users.Current.RamOwnerId);
    }

    [Fact]
    public void ExistingRamSessionCannotBeReusedByAnotherSubject()
    {
        var context = new DefaultHttpContext { User = Principal("user-1") };
        var users = new TravelUserAccessor(new HttpContextAccessor { HttpContext = context },
            TravelUserDirectory.Parse(null), User("legacy"), TravelAuthenticationMode.OpenIdConnect, true);
        var resolver = new RamIdentityResolver(users);
        var session = new Session();

        Assert.Equal("user-1", resolver.Resolve(session).OwnerId);
        context.User = Principal("user-2");

        Assert.Throws<UnauthorizedAccessException>(() => resolver.Resolve(session));
    }

    [Theory]
    [InlineData("expired")]
    [InlineData("missing-token")]
    [InlineData("wrong-subject")]
    [InlineData("wrong-tenant")]
    [InlineData("wrong-traveler")]
    [InlineData("missing-ticket")]
    public async Task InvalidOrMismatchedTicketsCannotDelegate(string failure)
    {
        var principal = Principal("user-1");
        var ticketPrincipal = Principal(failure == "wrong-subject" ? "user-2" : "user-1",
            failure == "wrong-tenant" ? "other-tenant" : "tenant",
            failure == "wrong-traveler" ? "other-traveler" : "traveler");
        using var services = Services(ticketPrincipal, failure);

        await Assert.ThrowsAsync<ContextRetrieverAccessDeniedException>(async () =>
            await new TravelRamDelegatedTokenProvider(Accessor(principal, services)).GetTokenAsync());
    }

    [Fact]
    public async Task MissingRequestContextCannotDelegate()
    {
        var users = new TravelUserAccessor(new HttpContextAccessor(), TravelUserDirectory.Parse(null),
            User("legacy"), TravelAuthenticationMode.OpenIdConnect, true);
        await Assert.ThrowsAsync<ContextRetrieverAccessDeniedException>(async () =>
            await new TravelRamDelegatedTokenProvider(users).GetTokenAsync());
    }

    private static TravelUser User(string subject) => new(subject, "Demo", "tenant", "traveler", "", null, null, null, null, null);

    private static ClaimsPrincipal Principal(string subject, string tenant = "tenant", string traveler = "traveler") => new(new ClaimsIdentity([
        new Claim(ClaimTypes.NameIdentifier, subject),
        new Claim(TravelUserDirectory.TenantClaim, tenant),
        new Claim(TravelUserDirectory.TravelerClaim, traveler),
        new Claim(TravelAuthentication.AccessTokenVersionClaim, "token-version"),
    ], "oidc"));

    private static TravelUserAccessor Accessor(ClaimsPrincipal principal, IServiceProvider services) => new(
        new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = principal, RequestServices = services } },
        TravelUserDirectory.Parse(null), User("legacy"), TravelAuthenticationMode.OpenIdConnect, true);

    private static ServiceProvider Services(ClaimsPrincipal principal, string? failure = null)
    {
        var properties = new AuthenticationProperties();
        properties.StoreTokens([
            new AuthenticationToken { Name = "access_token", Value = failure == "missing-token" ? "" : "signed-access-token" },
            new AuthenticationToken { Name = "expires_at", Value = DateTimeOffset.UtcNow.AddMinutes(failure == "expired" ? -5 : 5).ToString("O") },
        ]);
        var ticket = new AuthenticationTicket(principal, properties, CookieAuthenticationDefaults.AuthenticationScheme);
        var result = failure == "missing-ticket" ? AuthenticateResult.NoResult() : AuthenticateResult.Success(ticket);
        return new ServiceCollection().AddSingleton<IAuthenticationService>(new Authentication(result)).BuildServiceProvider();
    }

    private sealed class Session : AgentSession;

    private sealed class Authentication(AuthenticateResult result) : IAuthenticationService
    {
        public Task<AuthenticateResult> AuthenticateAsync(HttpContext context, string? scheme) => Task.FromResult(result);
        public Task ChallengeAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => throw new NotSupportedException();
        public Task ForbidAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => throw new NotSupportedException();
        public Task SignInAsync(HttpContext context, string? scheme, ClaimsPrincipal principal, AuthenticationProperties? properties) => throw new NotSupportedException();
        public Task SignOutAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => throw new NotSupportedException();
    }
}
