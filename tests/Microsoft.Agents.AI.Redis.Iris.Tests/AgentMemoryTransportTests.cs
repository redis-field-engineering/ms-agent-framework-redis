using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Agents.AI.Redis.Iris;
using Microsoft.Extensions.DependencyInjection;
using Redis.Iris.AgentMemory;
using Redis.Iris.ContextRetriever;
using Xunit;

namespace Microsoft.Agents.AI.Redis.Iris.Tests;

public class AgentMemoryTransportTests
{
    [Fact]
    public async Task RamDiClientRejectsRedirectWithoutForwardingUserToken()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var original = new TcpListener(IPAddress.Loopback, 0);
        using var target = new TcpListener(IPAddress.Loopback, 0);
        original.Start(); target.Start();
        var originalPort = ((IPEndPoint)original.LocalEndpoint).Port;
        var targetPort = ((IPEndPoint)target.LocalEndpoint).Port;
        var first = ReplyAsync(original, $"HTTP/1.1 302 Found\r\nLocation: http://127.0.0.1:{targetPort}/redirected\r\nContent-Length: 0\r\nConnection: close\r\n\r\n", timeout.Token);
        var services = new ServiceCollection();
        services.AddRedisIrisAgentMemory(o =>
        {
            o.Endpoint = new($"http://127.0.0.1:{originalPort}/");
            o.StoreId = "store"; o.Credential = "synthetic-agent-key";
            o.IdentityResolver = _ => new("tenant", "owner", "session", "owner", "agent");
            o.AuthorizationPolicy = _ => true;
            o.DelegatedTokenProvider = new FixedAgentMemoryDelegatedTokenProvider(new("synthetic-user-token", DateTimeOffset.UtcNow.AddHours(1)));
        });
        using var provider = services.BuildServiceProvider();
        var error = await Assert.ThrowsAsync<AgentMemoryException>(() =>
            provider.GetRequiredService<IRedisAgentMemoryClient>().GetSessionMemoryAsync("session", cancellationToken: timeout.Token));
        Assert.Equal(HttpStatusCode.Found, error.StatusCode);
        Assert.Contains("X-Iris-User-Token: synthetic-user-token", await first, StringComparison.OrdinalIgnoreCase);
        Assert.False(target.Pending());
    }

    [Fact]
    public void RemotePlainHttpIsRejectedByClientAndRegistration()
    {
        using var http = new HttpClient();
        Assert.Throws<ArgumentException>(() => new RedisAgentMemoryClient(http,
            new() { Endpoint = new("http://memory.example.test"), StoreId = "store" }, new FixedAgentMemoryCredentialProvider("key")));
        Assert.Throws<ArgumentException>(() => new ServiceCollection().AddRedisIrisAgentMemory(o =>
        {
            o.Endpoint = new("http://memory.example.test");
            o.StoreId = "store"; o.Credential = "key";
            o.IdentityResolver = _ => new("tenant", "owner", "session", "owner", "agent");
            o.AuthorizationPolicy = _ => true;
        }));
    }

    static async Task<string> ReplyAsync(TcpListener listener, string response, CancellationToken ct)
    {
        using var client = await listener.AcceptTcpClientAsync(ct);
        var stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
        var headers = new StringBuilder();
        while (await reader.ReadLineAsync(ct) is { Length: > 0 } line) headers.AppendLine(line);
        await stream.WriteAsync(Encoding.ASCII.GetBytes(response), ct);
        return headers.ToString();
    }

}
