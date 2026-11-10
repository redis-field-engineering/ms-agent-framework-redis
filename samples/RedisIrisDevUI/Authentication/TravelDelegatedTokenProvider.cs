// Copyright (c) Redis, Inc.

using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Redis.Iris.AgentMemory;
using Redis.Iris.ContextRetriever;

namespace RedisIrisDevUI;

internal sealed class TravelDelegatedTokenProvider(TravelUserAccessor users) : IContextRetrieverDelegatedTokenProvider
{
    public async ValueTask<ContextRetrieverDelegatedToken> GetTokenAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (users.AuthenticationMode == TravelAuthenticationMode.OpenIdConnect)
        {
            var httpContext = users.HttpContext
                ?? throw new ContextRetrieverAccessDeniedException("No authenticated HTTP caller is available.");
            var authentication = await httpContext.AuthenticateAsync(
                CookieAuthenticationDefaults.AuthenticationScheme).ConfigureAwait(false);
            if (!authentication.Succeeded || authentication.Principal is null || authentication.Properties is null)
            {
                throw new ContextRetrieverAccessDeniedException("The OIDC authentication ticket is unavailable.");
            }

            var current = users.Current;
            if (authentication.Principal.FindFirstValue(ClaimTypes.NameIdentifier) != current.Subject ||
                authentication.Principal.FindFirstValue(TravelUserDirectory.TenantClaim) != current.TenantId ||
                authentication.Principal.FindFirstValue(TravelUserDirectory.TravelerClaim) != current.TravelerId)
            {
                throw new ContextRetrieverAccessDeniedException("The authentication ticket does not belong to the current caller.");
            }

            var accessToken = authentication.Properties.GetTokenValue("access_token");
            var expiresAtValue = authentication.Properties.GetTokenValue("expires_at");
            var version = authentication.Principal.FindFirstValue(TravelAuthentication.AccessTokenVersionClaim);
            if (string.IsNullOrWhiteSpace(accessToken) || string.IsNullOrWhiteSpace(version) ||
                !DateTimeOffset.TryParse(expiresAtValue, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var expiresAt))
            {
                throw new ContextRetrieverAccessDeniedException("The OIDC authentication ticket does not contain a complete delegated access token.");
            }

            var delegatedToken = new ContextRetrieverDelegatedToken(accessToken, version, expiresAt);
            delegatedToken.Validate();
            return delegatedToken;
        }

        var user = users.Current;
        var token = new ContextRetrieverDelegatedToken(
            user.ContextRetrieverDelegatedToken
                ?? throw new ContextRetrieverAccessDeniedException("No delegated Context Retriever token is available for this user."),
            user.ContextRetrieverDelegatedTokenVersion
                ?? throw new ContextRetrieverAccessDeniedException("No delegated Context Retriever token version is available for this user."),
            user.ContextRetrieverDelegatedTokenExpiresAt
                ?? throw new ContextRetrieverAccessDeniedException("No delegated Context Retriever token expiry is available for this user."));
        token.Validate();
        return token;
    }
}

internal sealed class TravelRamDelegatedTokenProvider(TravelUserAccessor users) : IAgentMemoryDelegatedTokenProvider
{
    private readonly TravelDelegatedTokenProvider _tokens = new(users);

    public async ValueTask<AgentMemoryDelegatedToken> GetTokenAsync(CancellationToken cancellationToken = default)
    {
        if (users.AuthenticationMode != TravelAuthenticationMode.OpenIdConnect)
        {
            throw new UnauthorizedAccessException("RAM delegation requires an authenticated OIDC caller.");
        }

        // Use the same validated server-side ticket as CR; never read a token from request input.
        var token = await this._tokens.GetTokenAsync(cancellationToken).ConfigureAwait(false);
        var delegated = new AgentMemoryDelegatedToken(token.Token, token.ExpiresAt);
        delegated.Validate();
        return delegated;
    }
}
