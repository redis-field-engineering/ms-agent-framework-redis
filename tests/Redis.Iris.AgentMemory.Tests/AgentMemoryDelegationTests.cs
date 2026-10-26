using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;

namespace Redis.Iris.AgentMemory.Tests;

public sealed class AgentMemoryDelegationTests
{
    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task EveryOperationSendsBothCredentialsWithoutRetryingAuthorizationFailures(HttpStatusCode status)
    {
        var handler = new Handler(status);
        var tokens = new Tokens();
        var client = Client(handler, tokens);
        Func<Task>[] operations = [
            () => client.AddSessionEventAsync(new()
            {
                SessionId = "session", ActorId = "owner", Role = SessionEventRole.USER,
                Content = [new("test")], CreatedAt = DateTimeOffset.UtcNow,
            }),
            () => client.GetSessionMemoryAsync("session"),
            () => client.TryGetSessionMemoryAsync("session"),
            () => client.GetSessionEventAsync("session", "event"),
            () => client.DeleteSessionEventAsync("session", "event"),
            () => client.DeleteSessionMemoryAsync("session"),
            () => client.ListSessionsAsync("owner"),
            () => client.SearchLongTermMemoryAsync(new() { Text = "test" }),
            () => client.BulkDeleteLongTermMemoriesAsync(["memory"]),
        ];

        foreach (var operation in operations)
        {
            var error = await Assert.ThrowsAsync<AgentMemoryException>(operation);
            Assert.Equal(status, error.StatusCode);
            Assert.DoesNotContain("user-token", error.ToString());
        }

        Assert.Equal(operations.Length, handler.Requests.Count);
        Assert.Equal(operations.Length, tokens.Calls);
        Assert.All(handler.Requests, r =>
        {
            Assert.Equal("Bearer agent-key", r.Authorization);
            Assert.Equal("user-token", r.UserToken);
        });
    }

    [Fact]
    public async Task ConcurrentCallersAndTokenRotationDoNotShareHeaders()
    {
        var caller = new AsyncLocal<string>();
        var handler = new Handler(HttpStatusCode.NoContent);
        using var http = new HttpClient(handler);
        var tokens = new Tokens(() => new(caller.Value!, DateTimeOffset.UtcNow.AddMinutes(5)));
        var client = new RedisAgentMemoryClient(http, Options(), new FixedAgentMemoryCredentialProvider("agent-key"), tokens);

        await Task.WhenAll(Enumerable.Range(0, 20).Select(async i =>
        {
            caller.Value = $"user-{i}";
            await client.DeleteSessionMemoryAsync($"user-{i}");
            caller.Value = $"rotated-{i}";
            await client.DeleteSessionMemoryAsync($"rotated-{i}");
        }));

        Assert.Equal(40, handler.Requests.Count);
        Assert.All(handler.Requests, r => Assert.EndsWith("/" + r.UserToken, r.Path));
        Assert.False(http.DefaultRequestHeaders.Contains("X-Iris-User-Token"));
        Assert.Null(http.DefaultRequestHeaders.Authorization);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("expired")]
    [InlineData("missing")]
    [InlineData("header-injection")]
    public async Task InvalidProviderOutputFailsBeforeSending(string failure)
    {
        var handler = new Handler(HttpStatusCode.NoContent);
        var tokens = new Tokens(() => failure switch
        {
            "empty" => new("", DateTimeOffset.UtcNow.AddMinutes(5)),
            "expired" => new("secret-expired", DateTimeOffset.UtcNow.AddMinutes(-5)),
            "header-injection" => new("secret\r\nX-Test: injected", DateTimeOffset.UtcNow.AddMinutes(5)),
            _ => null!,
        });

        var error = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Client(handler, tokens).DeleteSessionMemoryAsync("session"));

        Assert.Empty(handler.Requests);
        Assert.DoesNotContain("secret", error.ToString());
    }

    [Fact]
    public async Task ProviderFailureAndCancellationNeverFallBackToTheAgentKey()
    {
        var handler = new Handler(HttpStatusCode.NoContent);
        var denied = Client(handler, new Tokens(() => throw new UnauthorizedAccessException("Sign in again.")));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => denied.DeleteSessionMemoryAsync("session"));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Client(handler, new Tokens()).DeleteSessionMemoryAsync("session", cancellation.Token));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ExistingConstructorStillUsesOnlyAnAgentKey()
    {
        var handler = new Handler(HttpStatusCode.NoContent);
        var client = new RedisAgentMemoryClient(new HttpClient(handler), Options(), new FixedAgentMemoryCredentialProvider("agent-key"));
        await client.DeleteSessionMemoryAsync("session");
        Assert.Null(Assert.Single(handler.Requests).UserToken);
    }

    [Fact]
    public void TokenDisplayAndDefaultJsonSerializationExcludeTheSecret()
    {
        var token = new AgentMemoryDelegatedToken("private-user-token", DateTimeOffset.UtcNow.AddMinutes(5));
        Assert.DoesNotContain(token.Token, token.ToString());
        Assert.DoesNotContain(token.Token, JsonSerializer.Serialize(token));
    }

    private static RedisAgentMemoryClientOptions Options() => new() { Endpoint = new("https://memory.example"), StoreId = "store" };
    private static RedisAgentMemoryClient Client(HttpMessageHandler handler, IAgentMemoryDelegatedTokenProvider tokens) =>
        new(new HttpClient(handler), Options(), new FixedAgentMemoryCredentialProvider("agent-key"), tokens);

    private sealed class Tokens(Func<AgentMemoryDelegatedToken>? value = null) : IAgentMemoryDelegatedTokenProvider
    {
        public int Calls;
        public async ValueTask<AgentMemoryDelegatedToken> GetTokenAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref this.Calls);
            await Task.Yield();
            return value is null ? new("user-token", DateTimeOffset.UtcNow.AddMinutes(5)) : value();
        }
    }

    private sealed class Handler(HttpStatusCode status) : HttpMessageHandler
    {
        public ConcurrentQueue<(string Path, string? Authorization, string? UserToken)> Requests { get; } = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Yield();
            this.Requests.Enqueue((request.RequestUri!.AbsolutePath, request.Headers.Authorization?.ToString(),
                request.Headers.TryGetValues("X-Iris-User-Token", out var values) ? Assert.Single(values) : null));
            return new(status) { Content = new StringContent("{}") };
        }
    }
}
