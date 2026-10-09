// Copyright (c) Redis, Inc.

using System.Collections.Concurrent;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace RedisIrisDevUI;

internal enum TravelAuthenticationMode
{
    Token,
    OpenIdConnect,
}

internal static class TravelAuthentication
{
    public const string OpenIdConnectScheme = "Cognito";
    public const string AccessTokenVersionClaim = "redis_iris:access_token_version";

    public static TravelAuthenticationMode ParseMode(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        null or "" or "token" => TravelAuthenticationMode.Token,
        "oidc" => TravelAuthenticationMode.OpenIdConnect,
        _ => throw new InvalidOperationException("DEVUI_AUTH_MODE must be token or oidc."),
    };

    public static void ApplyValidatedAccessTokenClaims(
        ClaimsPrincipal principal,
        ClaimsIdentity accessTokenIdentity,
        string requiredScope)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(accessTokenIdentity);
        ArgumentException.ThrowIfNullOrWhiteSpace(requiredScope);

        var scopes = accessTokenIdentity.FindAll("scope")
            .SelectMany(static claim => claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (!scopes.Contains(requiredScope, StringComparer.Ordinal))
        {
            throw new UnauthorizedAccessException("The Cognito access token does not contain the required Context Retriever scope.");
        }
        if (!string.Equals(RequiredClaim(accessTokenIdentity, "token_use"), "access", StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException("Cognito returned a token that is not an access token.");
        }

        var subject = RequiredClaim(accessTokenIdentity, "sub");
        var tenantId = RequiredClaim(accessTokenIdentity, TravelUserDirectory.TenantClaim);
        var travelerId = RequiredClaim(accessTokenIdentity, TravelUserDirectory.TravelerClaim);
        var tokenVersion = RequiredClaim(accessTokenIdentity, "jti");
        var destination = principal.Identity as ClaimsIdentity
            ?? throw new UnauthorizedAccessException("The OIDC principal does not contain a claims identity.");

        ReplaceClaim(destination, ClaimTypes.NameIdentifier, subject);
        ReplaceClaim(destination, TravelUserDirectory.TenantClaim, tenantId);
        ReplaceClaim(destination, TravelUserDirectory.TravelerClaim, travelerId);
        ReplaceClaim(destination, AccessTokenVersionClaim, tokenVersion);
    }

    private static string RequiredClaim(ClaimsIdentity identity, string type)
    {
        var value = identity.FindFirst(type)?.Value;
        return !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new UnauthorizedAccessException($"The Cognito access token is missing the required '{type}' claim.");
    }

    private static void ReplaceClaim(ClaimsIdentity identity, string type, string value)
    {
        foreach (var existing in identity.FindAll(type).ToArray())
        {
            identity.RemoveClaim(existing);
        }
        identity.AddClaim(new Claim(type, value));
    }
}

/// <summary>
/// Keeps authentication tickets, including raw OAuth tokens, in this process. The browser cookie contains only
/// an opaque lookup key. This matches the sample's existing process-local Agent Framework session store.
/// </summary>
internal sealed class InMemoryAuthenticationTicketStore : ITicketStore
{
    private readonly ConcurrentDictionary<string, AuthenticationTicket> _tickets = new(StringComparer.Ordinal);

    public Task<string> StoreAsync(AuthenticationTicket ticket)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        var key = Guid.NewGuid().ToString("N");
        _tickets[key] = ticket;
        return Task.FromResult(key);
    }

    public Task RenewAsync(string key, AuthenticationTicket ticket)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(ticket);
        _tickets[key] = ticket;
        return Task.CompletedTask;
    }

    public Task<AuthenticationTicket?> RetrieveAsync(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (!_tickets.TryGetValue(key, out var ticket)) return Task.FromResult<AuthenticationTicket?>(null);
        if (ticket.Properties.ExpiresUtc is { } expiresAt && expiresAt <= DateTimeOffset.UtcNow)
        {
            _tickets.TryRemove(key, out _);
            return Task.FromResult<AuthenticationTicket?>(null);
        }
        return Task.FromResult<AuthenticationTicket?>(ticket);
    }

    public Task RemoveAsync(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        _tickets.TryRemove(key, out _);
        return Task.CompletedTask;
    }
}
