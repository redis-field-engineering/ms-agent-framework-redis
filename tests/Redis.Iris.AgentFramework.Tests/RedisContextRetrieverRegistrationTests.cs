using Redis.Iris.ContextRetriever;

namespace Redis.Iris.AgentFramework.Tests;

public sealed class RedisContextRetrieverRegistrationTests
{
    [Fact]
    public async Task RejectsAmbiguousAndMissingIdentityModes()
    {
        await using var cache = new ContextRetrieverClientCache(new NeverFactory());
        var resolver = new DelegateContextRetrieverAccessResolver((_, _) => throw new NotSupportedException());
        var fixedProfile = new ContextRetrieverAccessProfile(new Uri("https://context.example.test/mcp"), "fixed", new FixedContextRetrieverCredentialProvider(new("key", "v1")));

        Assert.Throws<InvalidOperationException>(() => new RedisContextRetrieverRegistration().CreateProvider(cache));
        Assert.Throws<InvalidOperationException>(() => new RedisContextRetrieverRegistration { AccessResolver = resolver, FixedAccessProfile = fixedProfile }.CreateProvider(cache));
        Assert.NotNull(new RedisContextRetrieverRegistration { AccessResolver = resolver }.CreateProvider(cache));
    }

    private sealed class NeverFactory : IContextRetrieverClientFactory
    {
        public Task<IContextRetrieverClient> ConnectAsync(ContextRetrieverClientOptions options, string agentKey, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
