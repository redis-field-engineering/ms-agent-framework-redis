// Copyright (c) Redis, Inc.

using Microsoft.Agents.AI;
using Redis.Iris.AgentFramework;

namespace RedisIrisDevUI;

internal sealed class TravelMemoryToolsProvider(RedisIrisLongTermMemoryWriter writer, MemoryProposalStore proposals, TravelUserAccessor users) : AIContextProvider
{
    public override IReadOnlyList<string> StateKeys => [nameof(RedisIrisLongTermMemoryWriter)];

    protected override ValueTask<AIContext> ProvideAIContextAsync(InvokingContext context, CancellationToken cancellationToken = default)
    {
        var session = context.Session ?? throw new InvalidOperationException("Memory tools require an agent session.");
        var userKey = users.Current.IsolationKey;
        return new(new AIContext
        {
            Tools = RedisIrisMemoryTools.Create(writer, session, (proposal, token) =>
            {
                token.ThrowIfCancellationRequested();
                if (users.Current.IsolationKey != userKey) throw new UnauthorizedAccessException();
                proposals.Add(userKey, proposal, session);
                return Task.CompletedTask;
            }),
        });
    }
}
