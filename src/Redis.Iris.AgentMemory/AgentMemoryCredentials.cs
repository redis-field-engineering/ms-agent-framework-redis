// Copyright (c) Redis, Inc.

namespace Redis.Iris.AgentMemory;

/// <summary>Supplies a Redis Agent Memory bearer credential.</summary>
public interface IAgentMemoryCredentialProvider
{
    /// <summary>Gets the credential for the current request.</summary>
    ValueTask<string> GetCredentialAsync(CancellationToken cancellationToken = default);
}

/// <summary>Supplies one fixed Redis Agent Memory bearer credential.</summary>
public sealed class FixedAgentMemoryCredentialProvider(string credential) : IAgentMemoryCredentialProvider
{
    private readonly string _credential = !string.IsNullOrWhiteSpace(credential)
        ? credential
        : throw new ArgumentException("Credential must not be empty.", nameof(credential));

    /// <inheritdoc />
    public ValueTask<string> GetCredentialAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(this._credential);
    }
}

/// <summary>Configures a Redis Agent Memory data-plane client.</summary>
public sealed class RedisAgentMemoryClientOptions
{
    /// <summary>Gets or sets the absolute data-plane endpoint.</summary>
    public required Uri Endpoint { get; set; }

    /// <summary>Gets or sets the default store identifier.</summary>
    public required string StoreId { get; set; }
}
