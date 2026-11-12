// Copyright (c) Redis, Inc.

using System.Runtime.CompilerServices;
using Microsoft.Agents.AI;
using Redis.Iris.AgentFramework;

namespace RedisIrisDevUI;

internal sealed class RamIdentityResolver(TravelUserAccessor users)
{
    private readonly ConditionalWeakTable<AgentSession, RedisIrisIdentityContext> _identities = new();

    // Only direct editor actions have no AgentSession; conversation capture still requires one.
    public RedisIrisIdentityContext ResolveMemoryWriteIdentity(AgentSession? session)
    {
        if (session is not null) return Resolve(session);
        var user = users.Current;
        return new(user.TenantId, user.RamOwnerId, "memory-editor", user.TravelerId, TravelAgentRegistration.AgentName);
    }

    public RedisIrisIdentityContext Resolve(AgentSession? session)
    {
        ArgumentNullException.ThrowIfNull(session);
        var user = users.Current;
        var identity = this._identities.GetValue(session, _ =>
        {
            var created = new RedisIrisIdentityContext(
                TenantId: user.TenantId,
                OwnerId: user.RamOwnerId,
                SessionId: $"redis-iris-devui-{Guid.NewGuid():N}",
                ActorId: user.TravelerId,
                AgentId: TravelAgentRegistration.AgentName);
            Console.WriteLine($"RAM TENANT : {created.TenantId}");
            Console.WriteLine($"RAM USER   : {created.OwnerId}");
            Console.WriteLine($"RAM SESSION: {created.SessionId}");
            return created;
        });
        if (!string.Equals(identity.TenantId, user.TenantId, StringComparison.Ordinal) ||
            !string.Equals(identity.OwnerId, user.RamOwnerId, StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException("The agent session belongs to a different tenant or traveler.");
        }
        return identity;
    }
}
