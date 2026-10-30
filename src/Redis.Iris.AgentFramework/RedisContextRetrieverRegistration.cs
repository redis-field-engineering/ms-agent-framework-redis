using Redis.Iris.ContextRetriever;

namespace Redis.Iris.AgentFramework;

/// <summary>One mutually exclusive configuration boundary for dynamic or fixed Context Retriever identity.</summary>
public sealed class RedisContextRetrieverRegistration
{
    /// <summary>Recommended per-invocation identity resolver.</summary>
    public IContextRetrieverAccessResolver? AccessResolver { get; init; }
    /// <summary>Fixed service identity shared by every caller of the agent.</summary>
    public ContextRetrieverAccessProfile? FixedAccessProfile { get; init; }
    /// <summary>Shared transport policy.</summary>
    public RedisContextRetrieverOptions Options { get; init; } = new();

    /// <summary>Creates the recommended dynamic provider.</summary>
    public RedisContextRetrieverProvider CreateProvider(ContextRetrieverClientCache clients, bool ownsClientCache = false)
    {
        Validate(RegistrationMode.Dynamic);
        return new RedisContextRetrieverProvider(AccessResolver!, clients, Options, ownsClientCache);
    }

    /// <summary>Connects the fixed service identity and returns an explicitly owned lease.</summary>
    public Task<ContextRetrieverToolLease> ConnectFixedAsync(
        IContextRetrieverClientFactory clientFactory,
        CancellationToken cancellationToken = default)
    {
        Validate(RegistrationMode.Fixed);
        return RedisContextRetrieverTools.ConnectAsync(FixedAccessProfile!, clientFactory, Options, cancellationToken);
    }

    private void Validate(RegistrationMode requested)
    {
        if ((AccessResolver is null) == (FixedAccessProfile is null))
        {
            throw new InvalidOperationException("Configure exactly one Context Retriever identity mode: AccessResolver (recommended) or FixedAccessProfile.");
        }
        if (requested == RegistrationMode.Dynamic && AccessResolver is null) throw new InvalidOperationException("This registration is configured for fixed identity.");
        if (requested == RegistrationMode.Fixed && FixedAccessProfile is null) throw new InvalidOperationException("This registration is configured for dynamic identity.");
    }

    private enum RegistrationMode { Dynamic, Fixed }
}
