namespace Redis.Iris.ContextRetriever.Management;

/// <summary>Supplies an administrative API key from trusted deployment-host configuration.</summary>
public interface IContextRetrieverManagementCredentialProvider
{
    /// <summary>Gets the current admin key for one HTTP request. Never derive it from model arguments.</summary>
    ValueTask<string> GetCredentialAsync(CancellationToken cancellationToken = default);
}

/// <summary>A fixed administrative API key provider for deployment tools.</summary>
public sealed class FixedContextRetrieverManagementCredentialProvider : IContextRetrieverManagementCredentialProvider
{
    private readonly string _credential;

    /// <summary>Creates a provider without reading ctxctl configuration or process environment.</summary>
    public FixedContextRetrieverManagementCredentialProvider(string credential)
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

    internal static void Validate(string credential)
    {
        if (string.IsNullOrWhiteSpace(credential) || credential.Any(character => char.IsWhiteSpace(character) || char.IsControl(character)))
            throw new ArgumentException("A nonempty administrative API key without whitespace is required.");
    }
}
