using Redis.Iris.ContextRetriever;

namespace Microsoft.Agents.AI.Redis.Iris;

/// <summary>Resolves Context Retriever authorization from trusted per-invocation application context.</summary>
/// <remarks>The resolver must never derive identity or credentials from model-generated tool arguments.</remarks>
public interface IContextRetrieverAccessResolver
{
    /// <summary>Returns the access profile authorized for this invocation, or throws to fail closed.</summary>
    ValueTask<ContextRetrieverAccessProfile> ResolveAsync(
        AIContextProvider.InvokingContext context,
        CancellationToken cancellationToken = default);
}

/// <summary>Adapts a trusted application callback into an access resolver.</summary>
public sealed class DelegateContextRetrieverAccessResolver(
    Func<AIContextProvider.InvokingContext, CancellationToken, ValueTask<ContextRetrieverAccessProfile>> resolver)
    : IContextRetrieverAccessResolver
{
    /// <inheritdoc />
    public async ValueTask<ContextRetrieverAccessProfile> ResolveAsync(
        AIContextProvider.InvokingContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        var profile = await resolver(context, cancellationToken).ConfigureAwait(false)
            ?? throw new ContextRetrieverAccessDeniedException("Context Retriever access was not authorized.");
        profile.Validate();
        return profile;
    }
}
