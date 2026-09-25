// Copyright (c) Redis, Inc.

using Microsoft.Extensions.DependencyInjection;
using Redis.Iris.AgentMemory;

namespace Microsoft.Agents.AI.Redis.Iris.Tests;

public sealed class RedisIrisServiceCollectionExtensionsTests
{
    [Theory]
    [InlineData(true, RedisIrisEventWriter.ChatHistoryProvider)]
    [InlineData(false, RedisIrisEventWriter.MemoryProvider)]
    public void RegistrationSelectsExactlyOneEventWriter(bool useHistory, RedisIrisEventWriter expected)
    {
        var services = new ServiceCollection();
        services.AddRedisIrisAgentMemory(options => Valid(options, useHistory));

        using var provider = services.BuildServiceProvider();

        Assert.Equal(expected, provider.GetRequiredService<RedisIrisSessionEventWriter>().DesignatedWriter);
        Assert.NotNull(provider.GetRequiredService<RedisIrisMemoryProvider>());
        Assert.Equal(useHistory, provider.GetService<RedisIrisChatHistoryProvider>() is not null);
        Assert.NotNull(provider.GetRequiredService<IRedisAgentMemoryClient>());
    }

    [Fact]
    public void RegistrationReportsInvalidSettingWithoutCredentialValue()
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => new ServiceCollection().AddRedisIrisAgentMemory(options =>
        {
            Valid(options, true);
            options.Memory.Search = new() { SimilarityThreshold = 2 };
        }));

        Assert.Contains("SimilarityThreshold", exception.Message);
        Assert.DoesNotContain("test-secret", exception.Message);
    }

    private static void Valid(RedisIrisAgentMemoryOptions options, bool useHistory)
    {
        options.Endpoint = new("https://memory.example.test");
        options.StoreId = "store";
        options.Credential = "test-secret";
        options.UseChatHistoryProvider = useHistory;
        options.IdentityResolver = _ => new("tenant", "owner", "session", "actor", "agent", "namespace", ["topic"]);
        options.AuthorizationPolicy = _ => true;
    }
}
