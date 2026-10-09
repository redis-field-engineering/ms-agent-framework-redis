using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace RedisIrisDevUI.Tests;

public sealed class TravelAuthenticationTests
{
    private const string ContextScope = "https://redis-iris-context-retriever/context.read";

    [Fact]
    public void TokenAuthenticationRemainsTheDefault()
    {
        Assert.Equal(TravelAuthenticationMode.Token, TravelAuthentication.ParseMode(null));
        Assert.Equal(TravelAuthenticationMode.Token, TravelAuthentication.ParseMode("token"));
        Assert.Equal(TravelAuthenticationMode.OpenIdConnect, TravelAuthentication.ParseMode("oidc"));
    }

    [Fact]
    public void AccessTokenInspectorReturnsExactTokenAndDecodedJson()
    {
        var header = WebEncoders.Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(new { alg = "RS256", kid = "key-1" }));
        var payload = WebEncoders.Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(new
        {
            aud = "https://redis-iris-context-retriever",
            tenant_id = "redis-travel-demo",
            traveler_id = "traveler-001",
        }));
        var token = $"{header}.{payload}.signature";

        var inspected = TravelAuthentication.InspectAccessToken(token);

        Assert.Equal(token, inspected.Token);
        Assert.Equal("RS256", inspected.Header.GetProperty("alg").GetString());
        Assert.Equal("https://redis-iris-context-retriever", inspected.Claims.GetProperty("aud").GetString());
        Assert.Equal("traveler-001", inspected.Claims.GetProperty("traveler_id").GetString());
    }

    [Fact]
    public void AccessTokenInspectorRejectsMalformedToken()
    {
        Assert.Throws<FormatException>(() => TravelAuthentication.InspectAccessToken("not-a-jwt"));
    }

    [Fact]
    public void ValidatedAccessTokenClaimsReplaceUntrustedIdentityBoundary()
    {
        var destination = new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, "id-token-subject"),
            new Claim(TravelUserDirectory.TenantClaim, "wrong-tenant"),
            new Claim(TravelUserDirectory.TravelerClaim, "wrong-traveler"),
        }, "oidc");
        var principal = new ClaimsPrincipal(destination);
        var accessToken = new ClaimsIdentity(new[]
        {
            new Claim("sub", "cognito-user-1"),
            new Claim(TravelUserDirectory.TenantClaim, "redis-travel-demo"),
            new Claim(TravelUserDirectory.TravelerClaim, "traveler-001"),
            new Claim("jti", "jwt-id-1"),
            new Claim("token_use", "access"),
            new Claim("scope", $"openid {ContextScope}"),
        });

        TravelAuthentication.ApplyValidatedAccessTokenClaims(principal, accessToken, ContextScope);

        Assert.Equal("cognito-user-1", principal.FindFirstValue(ClaimTypes.NameIdentifier));
        Assert.Equal("redis-travel-demo", principal.FindFirstValue(TravelUserDirectory.TenantClaim));
        Assert.Equal("traveler-001", principal.FindFirstValue(TravelUserDirectory.TravelerClaim));
        Assert.Equal("jwt-id-1", principal.FindFirstValue(TravelAuthentication.AccessTokenVersionClaim));
    }

    [Fact]
    public void AccessTokenWithoutRequiredScopeIsRejected()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(authenticationType: "oidc"));
        var accessToken = new ClaimsIdentity(new[]
        {
            new Claim("sub", "cognito-user-1"),
            new Claim(TravelUserDirectory.TenantClaim, "redis-travel-demo"),
            new Claim(TravelUserDirectory.TravelerClaim, "traveler-001"),
            new Claim("jti", "jwt-id-1"),
            new Claim("token_use", "access"),
            new Claim("scope", "openid email"),
        });

        Assert.Throws<UnauthorizedAccessException>(() =>
            TravelAuthentication.ApplyValidatedAccessTokenClaims(principal, accessToken, ContextScope));
    }

    [Fact]
    public void OidcIdentityUsesTheValidatedTravelBoundaryVerbatim()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, "cognito-user-1"),
            new Claim(ClaimTypes.Name, "Avery Morgan"),
            new Claim(TravelUserDirectory.TenantClaim, "redis-travel-demo"),
            new Claim(TravelUserDirectory.TravelerClaim, "traveler-001"),
        }, "oidc"));
        var httpContext = new DefaultHttpContext { User = principal };
        var accessor = new TravelUserAccessor(
            new HttpContextAccessor { HttpContext = httpContext },
            TravelUserDirectory.Parse(null),
            LegacyUser(),
            TravelAuthenticationMode.OpenIdConnect);

        Assert.Equal("redis-travel-demo", accessor.Current.TenantId);
        Assert.Equal("traveler-001", accessor.Current.TravelerId);
        Assert.Equal("17-redis-travel-demo-12-traveler-001", accessor.Current.RamOwnerId);
    }

    [Fact]
    public async Task OidcDelegatedTokenComesFromTheServerSideAuthenticationTicket()
    {
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(10);
        var principal = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, "cognito-user-1"),
            new Claim(TravelUserDirectory.TenantClaim, "redis-travel-demo"),
            new Claim(TravelUserDirectory.TravelerClaim, "traveler-001"),
            new Claim(TravelAuthentication.AccessTokenVersionClaim, "jwt-id-1"),
        }, "oidc"));
        var properties = new AuthenticationProperties();
        properties.StoreTokens(new[]
        {
            new AuthenticationToken { Name = "access_token", Value = "signed-access-token" },
            new AuthenticationToken { Name = "expires_at", Value = expiresAt.ToString("O") },
        });
        var ticket = new AuthenticationTicket(principal, properties, CookieAuthenticationDefaults.AuthenticationScheme);
        var services = new ServiceCollection()
            .AddSingleton<IAuthenticationService>(new StubAuthenticationService(ticket))
            .BuildServiceProvider();
        var httpContext = new DefaultHttpContext { User = principal, RequestServices = services };
        var accessor = new TravelUserAccessor(
            new HttpContextAccessor { HttpContext = httpContext },
            TravelUserDirectory.Parse(null),
            LegacyUser(),
            TravelAuthenticationMode.OpenIdConnect);

        var token = await new TravelDelegatedTokenProvider(accessor).GetTokenAsync();

        Assert.Equal("signed-access-token", token.Token);
        Assert.Equal("jwt-id-1", token.Version);
        Assert.Equal(expiresAt, token.ExpiresAt);
    }

    [Fact]
    public async Task AuthenticationTicketStoreReturnsOnlyOpaqueKeysAndRemovesTickets()
    {
        var store = new InMemoryAuthenticationTicketStore();
        var ticket = new AuthenticationTicket(
            new ClaimsPrincipal(new ClaimsIdentity(authenticationType: "test")),
            CookieAuthenticationDefaults.AuthenticationScheme);

        var key = await store.StoreAsync(ticket);

        Assert.DoesNotContain("test", key, StringComparison.Ordinal);
        Assert.Same(ticket, await store.RetrieveAsync(key));
        await store.RemoveAsync(key);
        Assert.Null(await store.RetrieveAsync(key));
    }

    private static TravelUser LegacyUser() => new(
        "legacy",
        "Legacy",
        "legacy-tenant",
        "legacy-traveler",
        "legacy-token",
        null,
        null,
        null,
        null,
        null);

    private sealed class StubAuthenticationService(AuthenticationTicket ticket) : IAuthenticationService
    {
        public Task<AuthenticateResult> AuthenticateAsync(HttpContext context, string? scheme) =>
            Task.FromResult(AuthenticateResult.Success(ticket));

        public Task ChallengeAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) =>
            throw new NotSupportedException();

        public Task ForbidAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) =>
            throw new NotSupportedException();

        public Task SignInAsync(HttpContext context, string? scheme, ClaimsPrincipal principal, AuthenticationProperties? properties) =>
            throw new NotSupportedException();

        public Task SignOutAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) =>
            throw new NotSupportedException();
    }
}
