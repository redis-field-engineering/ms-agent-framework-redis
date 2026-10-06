// Copyright (c) Redis, Inc.

using System.Runtime.CompilerServices;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Redis.Iris;

namespace RedisIrisDevUI;

internal sealed class RamIdentityResolver(TravelUserAccessor users)
{
    private readonly ConditionalWeakTable<AgentSession, RedisIrisIdentityContext> _identities = new();

    public RedisIrisIdentityContext Resolve(AgentSession? session)
    {
        ArgumentNullException.ThrowIfNull(session);
        var user = users.Current;
        var identity = this._identities.GetValue(session, _ =>
        {
            var created = new RedisIrisIdentityContext(
                TenantId: user.TenantId,
                OwnerId: user.IsolationKey,
                SessionId: $"redis-iris-devui-{Guid.NewGuid():N}",
                ActorId: user.Subject,
                AgentId: "redis-iris-travel-assistant");
            Console.WriteLine($"RAM TENANT : {created.TenantId}");
            Console.WriteLine($"RAM USER   : {created.OwnerId}");
            Console.WriteLine($"RAM SESSION: {created.SessionId}");
            return created;
        });
        if (!string.Equals(identity.TenantId, user.TenantId, StringComparison.Ordinal) ||
            !string.Equals(identity.OwnerId, user.IsolationKey, StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException("The agent session belongs to a different tenant or traveler.");
        }
        return identity;
    }
}
