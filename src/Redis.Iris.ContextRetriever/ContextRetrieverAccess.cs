namespace Redis.Iris.ContextRetriever;

/// <summary>A short-lived Context Retriever credential.</summary>
/// <param name="AgentKey">Secret server-enforced agent identity.</param>
/// <param name="Version">Non-secret version used to rotate cached protocol sessions.</param>
/// <param name="ExpiresAt">Optional credential expiry.</param>
public sealed record ContextRetrieverCredential(string AgentKey, string Version, DateTimeOffset? ExpiresAt = null)
{
    /// <summary>Throws when the credential cannot be used.</summary>
    public void Validate(TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(AgentKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(Version);
        if (ExpiresAt is { } expiresAt && expiresAt <= (timeProvider ?? TimeProvider.System).GetUtcNow())
        {
            throw new ContextRetrieverAccessDeniedException("Context Retriever credential has expired.");
        }
    }
}

/// <summary>Gets a credential from a trusted application credential source.</summary>
public interface IContextRetrieverCredentialProvider
{
    /// <summary>Gets the current credential; implementations may rotate it between calls.</summary>
    ValueTask<ContextRetrieverCredential> GetCredentialAsync(CancellationToken cancellationToken = default);
}

/// <summary>Authorized non-secret access metadata plus its credential source.</summary>
/// <param name="Endpoint">Authorized local MCP endpoint.</param>
/// <param name="AccessProfileId">Stable non-secret security-principal identifier.</param>
/// <param name="CredentialProvider">Trusted credential provider.</param>
public sealed record ContextRetrieverAccessProfile(
    Uri Endpoint,
    string AccessProfileId,
    IContextRetrieverCredentialProvider CredentialProvider)
{
    /// <summary>Validates non-secret access metadata.</summary>
    public void Validate()
    {
        if (!Endpoint.IsAbsoluteUri) throw new ContextRetrieverAccessDeniedException("An absolute Context Retriever endpoint is required.");
        if (string.IsNullOrWhiteSpace(AccessProfileId)) throw new ContextRetrieverAccessDeniedException("A Context Retriever access profile is required.");
        ArgumentNullException.ThrowIfNull(CredentialProvider);
    }
}

/// <summary>A fixed credential provider intended for single-service identities and tests.</summary>
public sealed class FixedContextRetrieverCredentialProvider(ContextRetrieverCredential credential) : IContextRetrieverCredentialProvider
{
    /// <inheritdoc />
    public ValueTask<ContextRetrieverCredential> GetCredentialAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        credential.Validate();
        return ValueTask.FromResult(credential);
    }
}
