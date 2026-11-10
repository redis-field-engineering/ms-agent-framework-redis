// Copyright (c) Redis, Inc.

using System.Security.Claims;

namespace RedisIrisDevUI;

internal sealed class TravelUserAccessor(
    IHttpContextAccessor httpContextAccessor,
    TravelUserDirectory directory,
    TravelUser legacyUser,
    TravelAuthenticationMode authenticationMode,
    bool useSubjectForRamOwner = false)
{
    private readonly bool _useSubjectForRamOwner = ValidateRamMode(authenticationMode, useSubjectForRamOwner);

    public TravelAuthenticationMode AuthenticationMode { get; } = authenticationMode;

    public HttpContext? HttpContext => httpContextAccessor.HttpContext;

    public TravelUser Current => AuthenticationMode == TravelAuthenticationMode.OpenIdConnect
        ? ResolveOpenIdConnectUser(httpContextAccessor.HttpContext?.User) with { UseSubjectForRamOwner = this._useSubjectForRamOwner }
        : directory.IsMultiUser
            ? directory.Resolve(httpContextAccessor.HttpContext?.User
                ?? throw new UnauthorizedAccessException("No authenticated HTTP caller is available."))
            : legacyUser;

    private static bool ValidateRamMode(TravelAuthenticationMode mode, bool delegated)
    {
        if (delegated && mode != TravelAuthenticationMode.OpenIdConnect)
        {
            throw new InvalidOperationException("REDIS_IRIS_RAM_DELEGATED_AUTH requires DEVUI_AUTH_MODE=oidc.");
        }
        return delegated;
    }

    private static TravelUser ResolveOpenIdConnectUser(ClaimsPrincipal? principal)
    {
        if (principal?.Identity?.IsAuthenticated != true)
        {
            throw new UnauthorizedAccessException("No authenticated OIDC caller is available.");
        }

        var subject = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        var tenantId = principal.FindFirstValue(TravelUserDirectory.TenantClaim);
        var travelerId = principal.FindFirstValue(TravelUserDirectory.TravelerClaim);
        if (string.IsNullOrWhiteSpace(subject) || string.IsNullOrWhiteSpace(tenantId) || string.IsNullOrWhiteSpace(travelerId))
        {
            throw new UnauthorizedAccessException("The authenticated OIDC caller is missing its trusted travel boundary.");
        }

        return new TravelUser(
            subject,
            principal.Identity.Name ?? principal.FindFirstValue("email") ?? subject,
            tenantId,
            travelerId,
            SignInToken: string.Empty,
            ContextRetrieverAgentKey: null,
            ContextRetrieverCredentialVersion: null,
            ContextRetrieverDelegatedToken: null,
            ContextRetrieverDelegatedTokenVersion: null,
            ContextRetrieverDelegatedTokenExpiresAt: null);
    }
}
