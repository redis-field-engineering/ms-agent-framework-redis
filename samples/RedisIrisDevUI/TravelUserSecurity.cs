// Copyright (c) Redis, Inc.

using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Hosting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Redis.Iris.ContextRetriever;

namespace RedisIrisDevUI;

internal sealed record TravelUser(
    string Subject,
    string DisplayName,
    string TenantId,
    string TravelerId,
    string SignInToken,
    string? ContextRetrieverAgentKey,
    string? ContextRetrieverCredentialVersion,
    string? ContextRetrieverDelegatedToken,
    string? ContextRetrieverDelegatedTokenVersion,
    DateTimeOffset? ContextRetrieverDelegatedTokenExpiresAt)
{
    public string IsolationKey => $"{TenantId}:{TravelerId}";

    // RAM owner IDs use a storage-safe, unambiguous length-prefixed composite.
    public string RamOwnerId => $"{TenantId.Length}-{TenantId}-{TravelerId.Length}-{TravelerId}";
}

internal sealed record TravelSecurityBundle(
    string ContextRetrieverAgentKey,
    string ContextRetrieverCredentialVersion,
    IReadOnlyList<TravelUser> Users);

internal sealed record TravelUserProvisioningProfile(
    string Subject,
    string DisplayName,
    string TenantId,
    string TravelerId);

internal sealed class TravelUserDirectory
{
    internal const string TenantClaim = "tenant_id";
    internal const string TravelerClaim = "traveler_id";
    private readonly IReadOnlyList<TravelUser> _users;

    public ContextRetrieverCredential? AgentCredential { get; }

    private TravelUserDirectory(IReadOnlyList<TravelUser> users, ContextRetrieverCredential? agentCredential = null)
    {
        _users = users;
        AgentCredential = agentCredential;
    }

    public bool IsMultiUser => _users.Count > 0;

    public static TravelUserDirectory Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new([]);

        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        using var document = JsonDocument.Parse(json);
        var bundle = document.RootElement.ValueKind == JsonValueKind.Array
            ? null
            : JsonSerializer.Deserialize<TravelSecurityBundle>(json, options);
        var users = bundle?.Users ?? JsonSerializer.Deserialize<List<TravelUser>>(json, options)
            ?? throw new InvalidOperationException("DEVUI_USERS_JSON must contain users.");
        if (users.Count < 2)
        {
            throw new InvalidOperationException("DEVUI_USERS_JSON must contain at least two users for multi-user mode.");
        }

        foreach (var user in users)
        {
            if (string.IsNullOrWhiteSpace(user.Subject) || string.IsNullOrWhiteSpace(user.DisplayName) ||
                string.IsNullOrWhiteSpace(user.TenantId) || string.IsNullOrWhiteSpace(user.TravelerId) ||
                string.IsNullOrWhiteSpace(user.SignInToken) ||
                (!HasAgentCredential(user) && !HasDelegatedToken(user)))
            {
                throw new InvalidOperationException("Every DEVUI_USERS_JSON user field is required.");
            }

            if (HasDelegatedToken(user))
                new ContextRetrieverDelegatedToken(
                    user.ContextRetrieverDelegatedToken!,
                    user.ContextRetrieverDelegatedTokenVersion!,
                    user.ContextRetrieverDelegatedTokenExpiresAt!.Value).Validate();
        }

        if (users.Select(static user => user.Subject).Distinct(StringComparer.Ordinal).Count() != users.Count ||
            users.Select(static user => user.IsolationKey).Distinct(StringComparer.Ordinal).Count() != users.Count)
        {
            throw new InvalidOperationException(
                "DEVUI_USERS_JSON subjects and tenant/traveler boundaries must be unique.");
        }
        if (users.Select(static user => user.TenantId).Distinct(StringComparer.Ordinal).Count() != 1)
        {
            throw new InvalidOperationException("A DevUI tenant credential bundle cannot span multiple tenants.");
        }

        var credential = bundle is null ? null : new ContextRetrieverCredential(bundle.ContextRetrieverAgentKey, bundle.ContextRetrieverCredentialVersion);
        credential?.Validate();
        return new(users, credential);
    }

    private static bool HasAgentCredential(TravelUser user) =>
        !string.IsNullOrWhiteSpace(user.ContextRetrieverAgentKey) &&
        !string.IsNullOrWhiteSpace(user.ContextRetrieverCredentialVersion);

    private static bool HasDelegatedToken(TravelUser user) =>
        !string.IsNullOrWhiteSpace(user.ContextRetrieverDelegatedToken) &&
        !string.IsNullOrWhiteSpace(user.ContextRetrieverDelegatedTokenVersion) &&
        user.ContextRetrieverDelegatedTokenExpiresAt is not null;

    public static TravelUserDirectory Load(string? path, string? json)
    {
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
        {
            return Parse(File.ReadAllText(path));
        }

        return Parse(json);
    }

    public TravelUser? Authenticate(string candidateToken)
    {
        if (string.IsNullOrWhiteSpace(candidateToken)) return null;
        var candidateHash = SHA256.HashData(Encoding.UTF8.GetBytes(candidateToken));
        TravelUser? match = null;
        foreach (var user in _users)
        {
            var configuredHash = SHA256.HashData(Encoding.UTF8.GetBytes(user.SignInToken));
            if (CryptographicOperations.FixedTimeEquals(candidateHash, configuredHash)) match = user;
        }
        return match;
    }

    public TravelUser Resolve(ClaimsPrincipal principal)
    {
        var subject = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        var tenantId = principal.FindFirstValue(TenantClaim);
        var travelerId = principal.FindFirstValue(TravelerClaim);
        var user = _users.SingleOrDefault(user =>
            string.Equals(user.Subject, subject, StringComparison.Ordinal) &&
            string.Equals(user.TenantId, tenantId, StringComparison.Ordinal) &&
            string.Equals(user.TravelerId, travelerId, StringComparison.Ordinal));
        return user ?? throw new UnauthorizedAccessException("The authenticated user is not authorized for a travel boundary.");
    }

    public static ClaimsPrincipal CreatePrincipal(TravelUser user, string authenticationType)
    {
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, user.Subject),
            new Claim(ClaimTypes.Name, user.DisplayName),
            new Claim(TenantClaim, user.TenantId),
            new Claim(TravelerClaim, user.TravelerId),
        };
        return new(new ClaimsIdentity(claims, authenticationType));
    }
}

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

internal sealed class TravelUserAccessor(
    IHttpContextAccessor httpContextAccessor,
    TravelUserDirectory directory,
    TravelUser legacyUser,
    TravelAuthenticationMode authenticationMode)
{
    public TravelAuthenticationMode AuthenticationMode { get; } = authenticationMode;

    public HttpContext? HttpContext => httpContextAccessor.HttpContext;

    public TravelUser Current => AuthenticationMode == TravelAuthenticationMode.OpenIdConnect
        ? ResolveOpenIdConnectUser(httpContextAccessor.HttpContext?.User)
        : directory.IsMultiUser
            ? directory.Resolve(httpContextAccessor.HttpContext?.User
                ?? throw new UnauthorizedAccessException("No authenticated HTTP caller is available."))
            : legacyUser;

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

internal sealed class TravelAgentIsolationKeyProvider(TravelUserAccessor users) : AgentIsolationKeyProvider
{
    public override ValueTask<string?> GetIsolationKeyAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<string?>(users.Current.IsolationKey);
    }
}

internal sealed class TravelIdentityContextProvider(TravelUserAccessor users) : AIContextProvider
{
    public override IReadOnlyList<string> StateKeys => [];

    protected override ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var user = users.Current;
        return ValueTask.FromResult(new AIContext
        {
            Instructions =
                $"The authenticated user's exact tenant_id is '{user.TenantId}' and exact traveler_id is '{user.TravelerId}'. " +
                "When a Context Retriever condition uses either field, copy its exact value verbatim; never substitute pronouns or remove punctuation.",
        });
    }
}
