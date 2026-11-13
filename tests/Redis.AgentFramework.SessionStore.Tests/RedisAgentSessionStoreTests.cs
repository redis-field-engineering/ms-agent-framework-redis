// Copyright (c) Redis, Inc.

using System.Reflection;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Hosting;
using Microsoft.Extensions.AI;
using Redis.AgentFramework.SessionStore;
using Redis.Iris.AgentFramework;
using StackExchange.Redis;
using Xunit;

namespace Redis.AgentFramework.SessionStore.Tests;

public sealed class RedisAgentSessionStoreTests
{
    [Fact]
    public async Task PendingFrameworkApprovalCanBeCompletedAfterRestoration()
    {
        var database = MemoryDatabase.Create();
        var calls = 0;
        ChatClientAgent NewAgent() => new(new ApprovalChatClient(), new ChatClientAgentOptions
        {
            Id = "approval-agent",
            ChatOptions = new()
            {
                Tools = [new ApprovalRequiredAIFunction(AIFunctionFactory.Create(() => { calls++; return "done"; }, "run_action"))],
            },
        });
        var agent = NewAgent();
        var session = await agent.CreateSessionAsync();
        var response = await agent.RunAsync("Perform the action", session);
        var approval = Assert.Single(response.Messages.SelectMany(message => message.Contents).OfType<ToolApprovalRequestContent>());
        Assert.Equal(0, calls);
        await new RedisAgentSessionStore(database).SaveSessionAsync(agent, "approval-context", session);

        var recreated = NewAgent();
        var restored = await new RedisAgentSessionStore(database).GetSessionAsync(recreated, "approval-context");
        await recreated.RunAsync(new ChatMessage(ChatRole.User, [approval.CreateResponse(true)]), restored);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task RestoresProviderStateAcrossStoreAndAgentRecreationWithIndependentBranches()
    {
        var database = MemoryDatabase.Create();
        var agent = CreateAgent();
        var store = new RedisAgentSessionStore(database);
        var session = await store.GetSessionAsync(agent, "a2a-context");
        session.StateBag.SetValue("ram", new RedisIrisProviderState("ram-session", "owner", "agent") { TenantId = "tenant" });
        session.StateBag.SetValue("pending", new[] { "approval-request-1" });
        await store.SaveSessionAsync(agent, "a2a-context", session);

        var recreated = new RedisAgentSessionStore(database);
        var newAgent = CreateAgent();
        var first = await recreated.GetSessionAsync(newAgent, "a2a-context");
        var second = await recreated.GetSessionAsync(newAgent, "a2a-context");
        Assert.NotSame(first, second);
        Assert.Equal("ram-session", first.StateBag.GetValue<RedisIrisProviderState>("ram")!.SessionId);
        Assert.Equal("tenant", first.StateBag.GetValue<RedisIrisProviderState>("ram")!.TenantId);
        first.StateBag.GetValue<string[]>("pending")![0] = "changed";
        Assert.Equal("approval-request-1", second.StateBag.GetValue<string[]>("pending")![0]);
        var third = await recreated.GetSessionAsync(newAgent, "a2a-context");
        Assert.Equal("approval-request-1", third.StateBag.GetValue<string[]>("pending")![0]);
    }

    [Fact]
    public async Task OpaqueKeysAreFramedAndScopedByAgentAndPrefix()
    {
        var database = MemoryDatabase.Create();
        var store = new RedisAgentSessionStore(database);
        var agent = CreateAgent("a:b");
        var session = await agent.CreateSessionAsync();
        session.StateBag.SetValue("marker", "saved");
        await store.SaveSessionAsync(agent, "c", session);
        Assert.Equal(0, (await store.GetSessionAsync(CreateAgent("a"), "b:c")).StateBag.Count);
        Assert.Equal(0, (await new RedisAgentSessionStore(database, new() { KeyPrefix = "other:" }).GetSessionAsync(agent, "c")).StateBag.Count);
        foreach (var id in new[] { "", "a:/{}😀", new string('x', 10_000) })
        {
            await store.SaveSessionAsync(agent, id, session);
            Assert.Equal("saved", (await store.GetSessionAsync(agent, id)).StateBag.GetValue<string>("marker"));
        }
        Assert.Equal(4, ((MemoryDatabase)database).Values.Count);
    }

    [Fact]
    public async Task CallerIsolationPreventsCrossUserReadAndDelete()
    {
        var database = MemoryDatabase.Create();
        var identity = new TestIsolation { Key = "tenant:alice" };
        var store = new IsolationKeyScopedAgentSessionStore(new RedisAgentSessionStore(database), identity);
        var agent = CreateAgent();
        var session = await agent.CreateSessionAsync();
        session.StateBag.SetValue("marker", "alice");
        await store.SaveSessionAsync(agent, "same-context", session);
        identity.Key = "tenant:bob";
        Assert.Equal(0, (await store.GetSessionAsync(agent, "same-context")).StateBag.Count);
        await store.DeleteSessionAsync(agent, "same-context");
        identity.Key = "tenant:alice";
        Assert.Equal("alice", (await store.GetSessionAsync(agent, "same-context")).StateBag.GetValue<string>("marker"));
        await store.DeleteSessionAsync(agent, "same-context");
        await store.DeleteSessionAsync(agent, "same-context");
        Assert.Equal(0, (await store.GetSessionAsync(agent, "same-context")).StateBag.Count);
    }

    [Fact]
    public async Task ExpiryIsAppliedWithSaveAndOptionsAreSnapshotted()
    {
        var database = MemoryDatabase.Create();
        var options = new RedisAgentSessionStoreOptions { Expiry = TimeSpan.FromMinutes(5) };
        var store = new RedisAgentSessionStore(database, options);
        options.Expiry = TimeSpan.FromMinutes(1);
        var agent = CreateAgent();
        var session = await agent.CreateSessionAsync();
        await store.SaveSessionAsync(agent, "context", session);
        Assert.Equal(new Expiration(TimeSpan.FromMinutes(5)), ((MemoryDatabase)database).LastExpiry);
        await new RedisAgentSessionStore(database).SaveSessionAsync(agent, "context", await agent.CreateSessionAsync());
        Assert.Equal(Expiration.Default, ((MemoryDatabase)database).LastExpiry);
        Assert.Throws<ArgumentOutOfRangeException>(() => new RedisAgentSessionStore(database, new() { Expiry = TimeSpan.Zero }));
    }

    [Fact]
    public async Task CorruptionAndRedisErrorsDoNotStartNewSessions()
    {
        var database = MemoryDatabase.Create();
        var storage = (MemoryDatabase)database;
        var store = new RedisAgentSessionStore(database);
        var agent = CreateAgent();
        var session = await agent.CreateSessionAsync();
        await store.SaveSessionAsync(agent, "context", session);
        storage.Values[storage.Values.Keys.Single()] = "not-json";
        await Assert.ThrowsAnyAsync<JsonException>(() => store.GetSessionAsync(agent, "context").AsTask());
        storage.Failure = new RedisConnectionException(ConnectionFailureType.UnableToConnect, "unavailable");
        await Assert.ThrowsAsync<RedisConnectionException>(() => store.GetSessionAsync(agent, "context").AsTask());
        await Assert.ThrowsAsync<RedisConnectionException>(() => store.SaveSessionAsync(agent, "context", session).AsTask());
        await Assert.ThrowsAsync<RedisConnectionException>(() => store.DeleteSessionAsync(agent, "context").AsTask());
    }

    [Fact]
    public async Task PreCancelledOperationsDoNotTouchRedis()
    {
        var database = MemoryDatabase.Create();
        var store = new RedisAgentSessionStore(database);
        var agent = CreateAgent();
        var cancelled = new CancellationToken(true);
        var session = await agent.CreateSessionAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.GetSessionAsync(agent, "context", cancelled).AsTask());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SaveSessionAsync(agent, "context", session, cancelled).AsTask());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.DeleteSessionAsync(agent, "context", cancelled).AsTask());
        Assert.Equal(0, ((MemoryDatabase)database).Calls);
    }

    internal static ChatClientAgent CreateAgent(string id = "stable-agent") => new(new UnusedChatClient(), new ChatClientAgentOptions { Id = id });

    private sealed class TestIsolation : AgentIsolationKeyProvider
    {
        public string? Key { get; set; }
        public override ValueTask<string?> GetIsolationKeyAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(Key);
    }

    private sealed class UnusedChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class ApprovalChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var message = messages.Any(message => message.Contents.OfType<FunctionResultContent>().Any())
                ? new ChatMessage(ChatRole.Assistant, "Action completed.")
                : new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("call-1", "run_action", new Dictionary<string, object?>())]);
            return Task.FromResult(new ChatResponse(message));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}

// A narrow Redis command double; session serialization/deserialization uses the real framework.
public class MemoryDatabase : DispatchProxy
{
    public Dictionary<string, RedisValue> Values { get; } = new();
    public Expiration LastExpiry { get; private set; }
    public Exception? Failure { get; set; }
    public int Calls { get; private set; }
    public static IDatabase Create() => Create<IDatabase, MemoryDatabase>();

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        Calls++;
        if (Failure is not null) throw Failure;
        var key = ((RedisKey)args![0]!).ToString();
        switch (targetMethod!.Name)
        {
            case "StringGetAsync": return Task.FromResult(Values.GetValueOrDefault(key, RedisValue.Null));
            case "StringSetAsync":
                Values[key] = (RedisValue)args[1]!;
                LastExpiry = (Expiration)args[2]!;
                return Task.FromResult(true);
            case "KeyDeleteAsync": return Task.FromResult(Values.Remove(key));
            default: throw new NotSupportedException(targetMethod.Name);
        }
    }
}
