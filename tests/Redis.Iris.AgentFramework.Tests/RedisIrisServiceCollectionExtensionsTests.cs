// Copyright (c) Redis, Inc.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.AI;
using Redis.Iris.AgentMemory;

namespace Redis.Iris.AgentFramework.Tests;

public sealed class RedisIrisServiceCollectionExtensionsTests
{
    [Fact]
    public void RegistrationConfiguresToolMessageMapping()
    {
        var services = new ServiceCollection();
        services.AddRedisIrisAgentMemory(options =>
        {
            Valid(options, true);
            options.MessageMapping.StoreToolMessages = true;
        });
        using var provider = services.BuildServiceProvider();
        var mapper = provider.GetRequiredService<RedisIrisMessageMapper>();
        var request = mapper.ToSessionEventRequest(new(ChatRole.Tool, [new FunctionResultContent("call", "value")]),
            "session", "owner", "agent");
        Assert.NotNull(request?.Metadata);
    }

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

    [Fact]
    public void RegistrationRejectsUnknownApiMode()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ServiceCollection().AddRedisIrisAgentMemory(options =>
        {
            Valid(options, true);
            options.ApiMode = (AgentMemoryApiMode)99;
        }));
    }

    [Fact]
    public async Task RegistrationWiresDelegationAndRedactsBothCredentials()
    {
        var services = new ServiceCollection();
        services.AddRedisIrisAgentMemory(options =>
        {
            Valid(options, true);
            options.DelegatedTokenProvider = new FixedAgentMemoryDelegatedTokenProvider(
                new("expired-user-token", DateTimeOffset.UtcNow.AddMinutes(-1)));
        });
        using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<IRedisAgentMemoryClient>();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => client.DeleteSessionMemoryAsync("session"));
        var options = provider.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<Microsoft.Extensions.Http.HttpClientFactoryOptions>>()
            .Get("Redis.Iris.AgentMemory");
        Assert.True(options.ShouldRedactHeaderValue("Authorization"));
        Assert.True(options.ShouldRedactHeaderValue("X-Iris-User-Token"));
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
