// Copyright (c) Redis, Inc.

namespace Redis.Iris.AgentMemory.Management;

/// <summary>Supplies an administrative bearer token for the Iris control plane, never a runtime agent key.</summary>
public interface IAgentMemoryManagementCredentialProvider
{
    /// <summary>Gets the credential for this request.</summary>
    ValueTask<string> GetCredentialAsync(CancellationToken cancellationToken = default);
}

/// <summary>A fixed administrative credential. Never register this provider in an agent's tool surface.</summary>
public sealed class FixedAgentMemoryManagementCredentialProvider : IAgentMemoryManagementCredentialProvider
{
    private readonly string _credential;

    /// <summary>Creates a provider using the supplied control-plane bearer token.</summary>
    public FixedAgentMemoryManagementCredentialProvider(string credential)
    {
        Validate(credential);
        _credential = credential;
    }

    /// <inheritdoc />
    public ValueTask<string> GetCredentialAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(_credential);
    }

    internal static void Validate(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(character => char.IsWhiteSpace(character) || char.IsControl(character)))
            throw new ArgumentException("A nonempty administrative bearer token without whitespace is required.", nameof(value));
    }
}
