using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Redis.Iris.AgentMemory;

namespace Microsoft.Agents.AI.Redis.Iris.Tests;

public sealed class AgentMemoryLoggingTests
{
    [Theory]
    [InlineData(LogLevel.Information)]
    [InlineData(LogLevel.Trace)]
    public async Task DefaultRegistrationDoesNotLogIdentityPathsOrCredentials(LogLevel minimum)
    {
        var capture = new Capture();
        var handler = new Handler();
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.SetMinimumLevel(minimum).AddProvider(capture));
        services.AddRedisIrisAgentMemory(options =>
        {
            options.Endpoint = new("https://memory.example.test");
            options.StoreId = "private-store";
            options.Credential = "private-agent-key";
            options.DelegatedTokenProvider = new FixedAgentMemoryDelegatedTokenProvider(new("private-user-token", DateTimeOffset.UtcNow.AddMinutes(5)));
            options.IdentityResolver = _ => new("tenant", "owner", "session", "actor", "agent");
            options.AuthorizationPolicy = _ => true;
        });
        services.AddHttpClient("Redis.Iris.AgentMemory").ConfigurePrimaryHttpMessageHandler(() => handler);
        services.AddHttpClient("LoggingControl").ConfigurePrimaryHttpMessageHandler(() => new Handler());
        using var provider = services.BuildServiceProvider();
        using var control = provider.GetRequiredService<IHttpClientFactory>().CreateClient("LoggingControl");
        using var response = await control.GetAsync("https://example.test/positive-logging-control");
        var client = provider.GetRequiredService<IRedisAgentMemoryClient>();
        Assert.Null(await client.TryGetSessionMemoryAsync("private-session"));
        await Assert.ThrowsAsync<AgentMemoryException>(() => client.ListSessionsAsync("private-owner"));

        Assert.Equal(2, handler.Calls);
        var logs = string.Join("\n", capture.Messages);
        Assert.Contains("positive-logging-control", logs);
        foreach (var sensitive in new[] { "private-session", "private-owner", "private-store", "private-agent-key", "private-user-token" })
            Assert.DoesNotContain(sensitive, logs);
    }

    private sealed class Handler : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") });
        }
    }

    private sealed class Capture : ILoggerProvider, ILogger
    {
        public ConcurrentQueue<string> Messages { get; } = new();
        public ILogger CreateLogger(string categoryName) => this;
        public bool IsEnabled(LogLevel logLevel) => true;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) => Messages.Enqueue(formatter(state, exception));
        public void Dispose() { }
    }
}
