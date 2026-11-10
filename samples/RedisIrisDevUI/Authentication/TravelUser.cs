// Copyright (c) Redis, Inc.

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
    public bool UseSubjectForRamOwner { get; init; }

    public string IsolationKey => UseSubjectForRamOwner
        ? $"{TenantId}:{TravelerId}:{RamOwnerId}"
        : $"{TenantId}:{TravelerId}";

    // Key-only mode preserves the composite owner; delegated mode uses the signed subject verbatim.
    public string RamOwnerId
    {
        get
        {
            if (!UseSubjectForRamOwner) return $"{TenantId.Length}-{TenantId}-{TravelerId.Length}-{TravelerId}";
            if (string.IsNullOrEmpty(Subject) || Subject.Length > 64 ||
                Subject.Any(static c => !char.IsAsciiLetterOrDigit(c) && c != '-'))
            {
                throw new UnauthorizedAccessException("The validated subject is not a supported RAM owner ID.");
            }
            return Subject;
        }
    }
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
