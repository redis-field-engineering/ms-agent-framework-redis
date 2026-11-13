// Copyright (c) Redis, Inc.

using Microsoft.Agents.AI;
using Redis.Iris.AgentFramework;

namespace RedisIrisDevUI;

internal sealed class RamIdentityResolver(TravelUserAccessor users)
{
    private readonly ProviderSessionState<RedisIrisIdentityContext> _identities = new(
        _ => CreateIdentity(users.Current), nameof(RamIdentityResolver));

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
        var identity = this._identities.GetOrInitializeState(session);
        if (!string.Equals(identity.TenantId, user.TenantId, StringComparison.Ordinal) ||
            !string.Equals(identity.OwnerId, user.RamOwnerId, StringComparison.Ordinal) ||
            !string.Equals(identity.ActorId, user.TravelerId, StringComparison.Ordinal) ||
            !string.Equals(identity.AgentId, TravelAgentRegistration.AgentName, StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException("The agent session belongs to a different tenant or traveler.");
        }
        return identity;
    }

    private static RedisIrisIdentityContext CreateIdentity(TravelUser user)
    {
        var identity = new RedisIrisIdentityContext(user.TenantId, user.RamOwnerId,
            $"redis-iris-devui-{Guid.NewGuid():N}", user.TravelerId, TravelAgentRegistration.AgentName);
        Console.WriteLine($"RAM TENANT : {identity.TenantId}");
        Console.WriteLine($"RAM USER   : {identity.OwnerId}");
        Console.WriteLine($"RAM SESSION: {identity.SessionId}");
        return identity;
    }
}
