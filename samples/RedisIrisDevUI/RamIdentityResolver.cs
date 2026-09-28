// Copyright (c) Redis, Inc.

using System.Runtime.CompilerServices;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Redis.Iris;

namespace RedisIrisDevUI;

internal sealed class RamIdentityResolver(string ownerId)
{
    private readonly ConditionalWeakTable<AgentSession, RedisIrisIdentityContext> _identities = new();

    public RedisIrisIdentityContext Resolve(AgentSession? session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return this._identities.GetValue(session, _ =>
        {
            var identity = new RedisIrisIdentityContext(
                TenantId: "redis-iris-devui",
                OwnerId: ownerId,
                SessionId: $"redis-iris-devui-{Guid.NewGuid():N}",
                ActorId: ownerId,
                AgentId: "redis-iris-travel-assistant");
            Console.WriteLine($"RAM USER   : {identity.OwnerId}");
            Console.WriteLine($"RAM SESSION: {identity.SessionId}");
            return identity;
        });
    }
}
