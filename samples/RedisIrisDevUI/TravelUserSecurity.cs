// Copyright (c) Redis, Inc.

using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Agents.AI.Hosting;

namespace RedisIrisDevUI;

internal sealed record TravelUser(
    string Subject,
    string DisplayName,
    string TenantId,
    string TravelerId,
    string SignInToken,
    string ContextRetrieverAgentKey,
    string ContextRetrieverCredentialVersion)
{
    public string IsolationKey => $"{TenantId}:{TravelerId}";
}

internal sealed class TravelUserDirectory
{
    internal const string TenantClaim = "tenant_id";
    internal const string TravelerClaim = "traveler_id";
    private readonly IReadOnlyList<TravelUser> _users;

    private TravelUserDirectory(IReadOnlyList<TravelUser> users) => _users = users;

    public bool IsMultiUser => _users.Count > 0;

    public static TravelUserDirectory Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new([]);

        var users = JsonSerializer.Deserialize<List<TravelUser>>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidOperationException("DEVUI_USERS_JSON must contain a JSON array.");
        if (users.Count < 2)
        {
            throw new InvalidOperationException("DEVUI_USERS_JSON must contain at least two users for multi-user mode.");
        }

        foreach (var user in users)
        {
            if (string.IsNullOrWhiteSpace(user.Subject) || string.IsNullOrWhiteSpace(user.DisplayName) ||
                string.IsNullOrWhiteSpace(user.TenantId) || string.IsNullOrWhiteSpace(user.TravelerId) ||
                string.IsNullOrWhiteSpace(user.SignInToken) || string.IsNullOrWhiteSpace(user.ContextRetrieverAgentKey) ||
                string.IsNullOrWhiteSpace(user.ContextRetrieverCredentialVersion))
            {
                throw new InvalidOperationException("Every DEVUI_USERS_JSON user field is required.");
            }
        }

        if (users.Select(static user => user.Subject).Distinct(StringComparer.Ordinal).Count() != users.Count ||
            users.Select(static user => user.IsolationKey).Distinct(StringComparer.Ordinal).Count() != users.Count ||
            users.Select(static user => user.ContextRetrieverAgentKey).Distinct(StringComparer.Ordinal).Count() != users.Count)
        {
            throw new InvalidOperationException(
                "DEVUI_USERS_JSON subjects, tenant/traveler boundaries, and scoped Context Retriever agent keys must be unique.");
        }

        return new(users);
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
