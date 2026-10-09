// Copyright (c) Redis, Inc.

using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Agents.AI.Hosting;
using Redis.Iris.ContextRetriever;

namespace RedisIrisDevUI;

internal sealed record TravelUser(
    string Subject,
    string DisplayName,
    string TenantId,
    string TravelerId,
    string SignInToken,
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

        var bundle = JsonSerializer.Deserialize<TravelSecurityBundle>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidOperationException("DEVUI_USERS_JSON must contain a tenant credential bundle.");
        var users = bundle.Users ?? throw new InvalidOperationException("DEVUI_USERS_JSON must contain users.");
        if (users.Count < 2)
        {
            throw new InvalidOperationException("DEVUI_USERS_JSON must contain at least two users for multi-user mode.");
        }

        foreach (var user in users)
        {
            if (string.IsNullOrWhiteSpace(user.Subject) || string.IsNullOrWhiteSpace(user.DisplayName) ||
                string.IsNullOrWhiteSpace(user.TenantId) || string.IsNullOrWhiteSpace(user.TravelerId) ||
                string.IsNullOrWhiteSpace(user.SignInToken) || string.IsNullOrWhiteSpace(user.ContextRetrieverDelegatedToken) ||
                string.IsNullOrWhiteSpace(user.ContextRetrieverDelegatedTokenVersion) ||
                user.ContextRetrieverDelegatedTokenExpiresAt is null)
            {
                throw new InvalidOperationException("Every DEVUI_USERS_JSON user field is required.");
            }

            new ContextRetrieverDelegatedToken(
                user.ContextRetrieverDelegatedToken,
                user.ContextRetrieverDelegatedTokenVersion,
                user.ContextRetrieverDelegatedTokenExpiresAt.Value).Validate();
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

        var credential = new ContextRetrieverCredential(bundle.ContextRetrieverAgentKey, bundle.ContextRetrieverCredentialVersion);
        credential.Validate();
        return new(users, credential);
    }

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
    public ValueTask<ContextRetrieverDelegatedToken> GetTokenAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var user = users.Current;
        var token = new ContextRetrieverDelegatedToken(
            user.ContextRetrieverDelegatedToken
                ?? throw new ContextRetrieverAccessDeniedException("No delegated Context Retriever token is available for this user."),
            user.ContextRetrieverDelegatedTokenVersion
                ?? throw new ContextRetrieverAccessDeniedException("No delegated Context Retriever token version is available for this user."),
            user.ContextRetrieverDelegatedTokenExpiresAt
                ?? throw new ContextRetrieverAccessDeniedException("No delegated Context Retriever token expiry is available for this user."));
        token.Validate();
        return ValueTask.FromResult(token);
    }
}

internal sealed class TravelUserAccessor(
    IHttpContextAccessor httpContextAccessor,
    TravelUserDirectory directory,
    TravelUser legacyUser)
{
    public TravelUser Current => directory.IsMultiUser
        ? directory.Resolve(httpContextAccessor.HttpContext?.User
            ?? throw new UnauthorizedAccessException("No authenticated HTTP caller is available."))
        : legacyUser;
}

internal sealed class TravelAgentIsolationKeyProvider(TravelUserAccessor users) : AgentIsolationKeyProvider
{
    public override ValueTask<string?> GetIsolationKeyAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<string?>(users.Current.IsolationKey);
    }
}
