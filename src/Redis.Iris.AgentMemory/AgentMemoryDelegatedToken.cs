// Copyright (c) Redis, Inc.

using System.Text.Json.Serialization;

namespace Redis.Iris.AgentMemory;

/// <summary>A user access token from trusted application authentication state.</summary>
/// <remarks>The service validates its signature and claims. Expiry metadata must come from the authenticated ticket.</remarks>
public sealed class AgentMemoryDelegatedToken(string token, DateTimeOffset expiresAt)
{
    /// <summary>Gets the secret user JWT, without a Bearer prefix.</summary>
    [JsonIgnore]
    public string Token { get; } = token;

    /// <summary>Gets the token's expiration time.</summary>
    public DateTimeOffset ExpiresAt { get; } = expiresAt;

    /// <summary>Rejects absent, malformed header values, and expired tokens without disclosing them.</summary>
    public void Validate(TimeProvider? timeProvider = null)
    {
        if (string.IsNullOrWhiteSpace(this.Token) || this.Token.Any(static c => char.IsWhiteSpace(c) || char.IsControl(c)))
        {
            throw new UnauthorizedAccessException("A non-empty delegated user token without whitespace is required.");
        }

        if (this.ExpiresAt <= (timeProvider ?? TimeProvider.System).GetUtcNow())
        {
            throw new UnauthorizedAccessException("The Agent Memory delegated user token has expired.");
        }
    }

    /// <inheritdoc />
    public override string ToString() => "AgentMemoryDelegatedToken { Token = [REDACTED] }";
}

/// <summary>Resolves the current caller's access token independently for each RAM request.</summary>
public interface IAgentMemoryDelegatedTokenProvider
{
    /// <summary>Gets a token or throws when the caller cannot delegate. Never fall back to an agent key alone.</summary>
    ValueTask<AgentMemoryDelegatedToken> GetTokenAsync(CancellationToken cancellationToken = default);
}

/// <summary>Supplies an explicit user token for samples and tests.</summary>
public sealed class FixedAgentMemoryDelegatedTokenProvider(AgentMemoryDelegatedToken token) : IAgentMemoryDelegatedTokenProvider
{
    private readonly AgentMemoryDelegatedToken _token = token ?? throw new ArgumentNullException(nameof(token));

    /// <inheritdoc />
    public ValueTask<AgentMemoryDelegatedToken> GetTokenAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        this._token.Validate();
        return ValueTask.FromResult(this._token);
    }
}
