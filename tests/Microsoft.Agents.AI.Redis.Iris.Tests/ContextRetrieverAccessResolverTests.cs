using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Redis.Iris.ContextRetriever;
using System.Text.Json;

namespace Microsoft.Agents.AI.Redis.Iris.Tests;

public sealed class ContextRetrieverAccessResolverTests
{
    private static readonly AIAgent s_agent = new TestAgent();

    [Fact]
    public async Task ResolvesAgainForEveryInvocation()
    {
        var calls = 0;
        var resolver = new DelegateContextRetrieverAccessResolver((_, _) =>
            ValueTask.FromResult(Profile($"tenant-{++calls}")));

        var first = await resolver.ResolveAsync(new(s_agent, null, new()));
        var second = await resolver.ResolveAsync(new(s_agent, null, new()));

        Assert.Equal("tenant-1", first.AccessProfileId);
        Assert.Equal("tenant-2", second.AccessProfileId);
    }

    [Fact]
    public async Task DeniedAccessFailsClosed()
    {
        var resolver = new DelegateContextRetrieverAccessResolver((_, _) =>
            ValueTask.FromException<ContextRetrieverAccessProfile>(new ContextRetrieverAccessDeniedException("denied")));

        await Assert.ThrowsAsync<ContextRetrieverAccessDeniedException>(
            () => resolver.ResolveAsync(new(s_agent, null, new())).AsTask());
    }

    [Fact]
    public async Task ExpiredCredentialsAreRejectedAndCancellationPropagates()
    {
        var expired = new FixedContextRetrieverCredentialProvider(
            new("secret", "v1", DateTimeOffset.UtcNow.AddMinutes(-1)));
        await Assert.ThrowsAsync<ContextRetrieverAccessDeniedException>(() => expired.GetCredentialAsync().AsTask());

        using var source = new CancellationTokenSource();
        source.Cancel();
        var valid = new FixedContextRetrieverCredentialProvider(new("secret", "v2"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => valid.GetCredentialAsync(source.Token).AsTask());

        var expiredToken = new FixedContextRetrieverDelegatedTokenProvider(
            new("jwt", "jwt-v1", DateTimeOffset.UtcNow.AddMinutes(-1)));
        await Assert.ThrowsAsync<ContextRetrieverAccessDeniedException>(() => expiredToken.GetTokenAsync().AsTask());
    }

    private static ContextRetrieverAccessProfile Profile(string id) => new(
        new Uri("https://context.example.test/mcp"), id, new FixedContextRetrieverCredentialProvider(new("secret", "v1")));

    private sealed class TestAgent : AIAgent
    {
        protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        protected override ValueTask<JsonElement> SerializeSessionCoreAsync(AgentSession session, JsonSerializerOptions? jsonSerializerOptions = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(JsonElement serializedState, JsonSerializerOptions? jsonSerializerOptions = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        protected override Task<AgentResponse> RunCoreAsync(IEnumerable<ChatMessage> messages, AgentSession? session = null, AgentRunOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(IEnumerable<ChatMessage> messages, AgentSession? session = null, AgentRunOptions? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }
}
