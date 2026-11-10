// Copyright (c) Redis, Inc.

using Microsoft.Agents.AI;
using Redis.Iris.AgentFramework;
using Redis.Iris.ContextRetriever;

namespace RedisIrisDevUI;

internal sealed class TravelContextAccessResolver(SampleOptions settings, TravelUserAccessor users)
    : IContextRetrieverAccessResolver
{
    private readonly TravelDelegatedTokenProvider _delegatedTokens = new(users);

    public ValueTask<ContextRetrieverAccessProfile> ResolveAsync(
        AIContextProvider.InvokingContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        var user = users.Current;
        var effectiveCredential = !string.IsNullOrWhiteSpace(user.ContextRetrieverAgentKey)
            ? new ContextRetrieverCredential(user.ContextRetrieverAgentKey, user.ContextRetrieverCredentialVersion!)
            : settings.ContextRetrieverCredential ?? throw new ContextRetrieverAccessDeniedException("No Context Retriever agent credential is configured.");
        var useDelegatedToken = settings.Oidc is not null || !string.IsNullOrWhiteSpace(user.ContextRetrieverDelegatedToken);
        var profile = new ContextRetrieverAccessProfile(
            new Uri(settings.ContextRetrieverEndpoint),
            $"travel:{user.IsolationKey}",
            new FixedContextRetrieverCredentialProvider(effectiveCredential),
            new Dictionary<string, IReadOnlyDictionary<string, object?>>(StringComparer.Ordinal)
            {
                ["get_traveler_by_id"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["id"] = user.TravelerId,
                },
            },
            useDelegatedToken ? _delegatedTokens : null);
        profile.Validate();
        return ValueTask.FromResult(profile);
    }
}
