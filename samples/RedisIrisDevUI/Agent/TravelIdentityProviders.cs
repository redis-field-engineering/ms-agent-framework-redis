// Copyright (c) Redis, Inc.

using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Hosting;

namespace RedisIrisDevUI;

internal sealed class TravelAgentIsolationKeyProvider(TravelUserAccessor users) : AgentIsolationKeyProvider
{
    public override ValueTask<string?> GetIsolationKeyAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<string?>(users.Current.IsolationKey);
    }
}

internal sealed class TravelIdentityContextProvider(TravelUserAccessor users) : AIContextProvider
{
    public override IReadOnlyList<string> StateKeys => [];

    protected override ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var user = users.Current;
        return ValueTask.FromResult(new AIContext
        {
            Instructions =
                $"The authenticated user's exact tenant_id is '{user.TenantId}' and exact traveler_id is '{user.TravelerId}'. " +
                "When a Context Retriever condition uses either field, copy its exact value verbatim; never substitute pronouns or remove punctuation.",
        });
    }
}
