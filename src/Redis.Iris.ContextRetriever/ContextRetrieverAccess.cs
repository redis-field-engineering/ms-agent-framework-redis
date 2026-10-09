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

/// <summary>A request-principal token delegated to Context Retriever.</summary>
/// <param name="Token">Signed JWT issued for the authenticated application principal.</param>
/// <param name="Version">Non-secret identifier that changes whenever the JWT changes.</param>
/// <param name="ExpiresAt">JWT expiry used to bound authenticated MCP-session reuse.</param>
public sealed record ContextRetrieverDelegatedToken(string Token, string Version, DateTimeOffset ExpiresAt)
{
    /// <summary>Throws when the token cannot be used.</summary>
    public void Validate(TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Token);
        ArgumentException.ThrowIfNullOrWhiteSpace(Version);
        if (ExpiresAt <= (timeProvider ?? TimeProvider.System).GetUtcNow())
        {
            throw new ContextRetrieverAccessDeniedException("Context Retriever delegated token has expired.");
        }
    }
}

/// <summary>Gets the current user's delegated JWT from trusted application identity state.</summary>
public interface IContextRetrieverDelegatedTokenProvider
{
    /// <summary>Gets a JWT for the current principal; implementations may refresh it between calls.</summary>
    ValueTask<ContextRetrieverDelegatedToken> GetTokenAsync(CancellationToken cancellationToken = default);
}

/// <summary>Authorized non-secret access metadata plus its credential source.</summary>
/// <param name="Endpoint">Authorized local MCP endpoint.</param>
/// <param name="AccessProfileId">Stable non-secret security-principal identifier.</param>
/// <param name="CredentialProvider">Trusted credential provider.</param>
/// <param name="BoundToolArguments">Trusted arguments injected into named tools and hidden from the model.</param>
/// <param name="DelegatedTokenProvider">Optional request-principal JWT provider. The token is never exposed as a tool argument.</param>
/// <param name="BoundFilterValues">Trusted values that replace matching model-supplied tag-condition values before execution.</param>
public sealed record ContextRetrieverAccessProfile(
    Uri Endpoint,
    string AccessProfileId,
    IContextRetrieverCredentialProvider CredentialProvider,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>>? BoundToolArguments = null,
    IContextRetrieverDelegatedTokenProvider? DelegatedTokenProvider = null,
    IReadOnlyDictionary<string, object?>? BoundFilterValues = null)
{
    /// <summary>Validates non-secret access metadata.</summary>
    public void Validate()
    {
        if (!Endpoint.IsAbsoluteUri) throw new ContextRetrieverAccessDeniedException("An absolute Context Retriever endpoint is required.");
        if (string.IsNullOrWhiteSpace(AccessProfileId)) throw new ContextRetrieverAccessDeniedException("A Context Retriever access profile is required.");
        ArgumentNullException.ThrowIfNull(CredentialProvider);
        foreach (var (tool, arguments) in BoundToolArguments ?? new Dictionary<string, IReadOnlyDictionary<string, object?>>())
        {
            if (string.IsNullOrWhiteSpace(tool)) throw new ContextRetrieverAccessDeniedException("A bound tool name cannot be blank.");
            if (arguments.Keys.Any(string.IsNullOrWhiteSpace)) throw new ContextRetrieverAccessDeniedException($"Bound arguments for '{tool}' cannot have blank names.");
        }
        if ((BoundFilterValues ?? new Dictionary<string, object?>()).Keys.Any(string.IsNullOrWhiteSpace))
            throw new ContextRetrieverAccessDeniedException("A bound filter field cannot be blank.");
    }
}

/// <summary>A fixed delegated-token provider intended for samples and tests.</summary>
public sealed class FixedContextRetrieverDelegatedTokenProvider(ContextRetrieverDelegatedToken token) : IContextRetrieverDelegatedTokenProvider
{
    /// <inheritdoc />
    public ValueTask<ContextRetrieverDelegatedToken> GetTokenAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        token.Validate();
        return ValueTask.FromResult(token);
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
