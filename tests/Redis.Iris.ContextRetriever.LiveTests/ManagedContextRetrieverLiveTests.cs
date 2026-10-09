// Copyright (c) Redis, Inc.

using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Redis.Iris;
using Microsoft.Extensions.AI;
using Redis.Iris.ContextRetriever.Modeling;

namespace Redis.Iris.ContextRetriever.LiveTests;

public sealed class ManagedContextRetrieverLiveTests
{
    private static readonly AIAgent s_agent = new StubAgent();

    [ContextLiveFact]
    [Trait("Category", "Live")]
    public async Task TenantKeyAndDelegatedJwtRemainIsolatedByTraveler()
    {
        var configuration = ContextLiveConfiguration.Load();
        Assert.True(configuration.Users.Count >= 2, "At least two scoped users are required.");

        foreach (var user in configuration.Users)
        {
            await using var client = await configuration.ConnectAsync(user);
            var typed = new ContextRetrieverTypedClient(client, TestModel.Model);
            var page = await typed.InvokeAsync<TravelerPage>("filter_traveler", new Dictionary<string, object?>
            {
                ["tag_conditions"] = new[] { new { field = "tenant_id", value = user.TenantId } },
                ["limit"] = 100,
                ["offset"] = 0,
            });

            Assert.NotNull(page);
            var traveler = Assert.Single(page.Results);
            Assert.Equal(user.TravelerId, traveler.TravelerId);
            Assert.Equal(user.TenantId, traveler.TenantId);
        }
    }

    [ContextLiveFact]
    [Trait("Category", "Live")]
    public async Task GetByIdCannotCrossTheScopedTravelerBoundary()
    {
        var configuration = ContextLiveConfiguration.Load();
        var first = configuration.Users[0];
        var second = configuration.Users[1];
        await using var client = await configuration.ConnectAsync(first);
        var typed = new ContextRetrieverTypedClient(client, TestModel.Model);

        var own = await typed.GetByIdAsync<TravelerRecord>(first.TravelerId);
        Assert.NotNull(own);
        Assert.Equal(first.TravelerId, own.TravelerId);

        await Assert.ThrowsAsync<ContextRetrieverException>(() => typed.GetByIdAsync<TravelerRecord>(second.TravelerId));
    }

    [ContextLiveFact]
    [Trait("Category", "Live")]
    public async Task ProviderHidesAndInjectsTrustedTravelerIdentity()
    {
        var configuration = ContextLiveConfiguration.Load();
        var user = configuration.Users[0];
        var resolver = new DelegateContextRetrieverAccessResolver((_, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new ContextRetrieverAccessProfile(
                configuration.Endpoint,
                $"live:{user.TenantId}:{user.TravelerId}",
                new FixedContextRetrieverCredentialProvider(new(configuration.AgentKey, configuration.CredentialVersion)),
                new Dictionary<string, IReadOnlyDictionary<string, object?>>(StringComparer.Ordinal)
                {
                    ["get_traveler_by_id"] = new Dictionary<string, object?> { ["id"] = user.TravelerId },
                },
                new FixedContextRetrieverDelegatedTokenProvider(new(user.DelegatedToken, user.DelegatedTokenVersion, user.DelegatedTokenExpiresAt))));
        });
        await using var cache = new ContextRetrieverClientCache(new ContextRetrieverClientFactory());
        var provider = new RedisContextRetrieverProvider(resolver, cache, new RedisContextRetrieverOptions
        {
            AuthorizationOwnedFilterFields = ["tenant_id", "traveler_id"],
        });

        var context = await provider.InvokingAsync(new(s_agent, null, new()));
        var tools = context.Tools!;
        var get = Assert.IsAssignableFrom<AIFunction>(tools.Single(tool => tool.Name == "get_traveler_by_id"));
        Assert.False(get.JsonSchema.GetProperty("properties").TryGetProperty("id", out _));

        var result = await get.InvokeAsync(new(new Dictionary<string, object?> { ["id"] = "17redistraveldemo12traveler001" }));
        var traveler = ExtractTextPayload(result).Deserialize<TravelerRecord>(JsonOptions);
        Assert.NotNull(traveler);
        Assert.Equal(user.TravelerId, traveler.TravelerId);

        var filter = Assert.IsAssignableFrom<AIFunction>(tools.Single(tool => tool.Name == "filter_traveler"));
        var enumValues = filter.JsonSchema.GetProperty("properties").GetProperty("tag_conditions")
            .GetProperty("items").GetProperty("properties").GetProperty("field").GetProperty("enum")
            .EnumerateArray().Select(item => item.GetString()).ToArray();
        Assert.DoesNotContain("tenant_id", enumValues);
        Assert.DoesNotContain("traveler_id", enumValues);

        var attack = new AIFunctionArguments(new Dictionary<string, object?>
        {
            ["tag_conditions"] = new[] { new { field = "traveler_id", value = "traveler-002" } },
        });
        await Assert.ThrowsAsync<ContextRetrieverAccessDeniedException>(() => filter.InvokeAsync(attack).AsTask());
    }

    private static JsonElement ExtractTextPayload(object? result)
    {
        if (result is TextContent textContent)
            return JsonDocument.Parse(textContent.Text).RootElement.Clone();
        var root = result is JsonElement element ? element : JsonSerializer.SerializeToElement(result, JsonOptions);
        if (root.TryGetProperty("isError", out var isError) && isError.ValueKind == JsonValueKind.True)
            throw new ContextRetrieverException("Managed Context Retriever returned an application error.");
        foreach (var block in root.GetProperty("content").EnumerateArray())
        {
            if (block.GetProperty("type").GetString() == "text")
                return JsonDocument.Parse(block.GetProperty("text").GetString()!).RootElement.Clone();
        }
        throw new ContextRetrieverException("Managed Context Retriever returned no JSON text payload.");
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private sealed class StubAgent : AIAgent
    {
        protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        protected override ValueTask<JsonElement> SerializeSessionCoreAsync(AgentSession session, JsonSerializerOptions? jsonSerializerOptions = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(JsonElement serializedState, JsonSerializerOptions? jsonSerializerOptions = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        protected override Task<AgentResponse> RunCoreAsync(IEnumerable<ChatMessage> messages, AgentSession? session = null, AgentRunOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(IEnumerable<ChatMessage> messages, AgentSession? session = null, AgentRunOptions? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default) { await Task.CompletedTask; yield break; }
    }
}

[ContextEntity(Name = "Traveler", KeyTemplate = "traveler:{id}")]
internal sealed record TravelerRecord(
    [property: ContextField(Name = "id", IsKeyComponent = true)] string Id,
    [property: ContextField(Name = "tenant_id", Index = ContextIndex.Tag)][property: JsonPropertyName("tenant_id")] string TenantId,
    [property: ContextField(Name = "traveler_id", Index = ContextIndex.Tag)][property: JsonPropertyName("traveler_id")] string TravelerId,
    [property: ContextField(Name = "full_name", Index = ContextIndex.Text)][property: JsonPropertyName("full_name")] string FullName);

internal sealed record TravelerPage(
    [property: JsonPropertyName("count")] int Count,
    [property: JsonPropertyName("results")] IReadOnlyList<TravelerRecord> Results);

internal static class TestModel
{
    public static ContextDataModel Model { get; } = ContextModelCompiler.Compile("Live travel model", "", typeof(TravelerRecord));
}

internal sealed record ContextLiveUser(
    string TenantId,
    string TravelerId,
    string DelegatedToken,
    string DelegatedTokenVersion,
    DateTimeOffset DelegatedTokenExpiresAt);

internal sealed record ContextLiveConfiguration(
    Uri Endpoint,
    string AgentKey,
    string CredentialVersion,
    IReadOnlyList<ContextLiveUser> Users)
{
    public static ContextLiveConfiguration Load()
    {
        var path = ContextLiveFactAttribute.ResolveUsersFile();
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var users = document.RootElement.GetProperty("users").EnumerateArray().Select(item => new ContextLiveUser(
            item.GetProperty("tenantId").GetString()!,
            item.GetProperty("travelerId").GetString()!,
            item.GetProperty("contextRetrieverDelegatedToken").GetString()!,
            item.GetProperty("contextRetrieverDelegatedTokenVersion").GetString()!,
            item.GetProperty("contextRetrieverDelegatedTokenExpiresAt").GetDateTimeOffset())).ToArray();
        return new(
            new Uri(Environment.GetEnvironmentVariable("REDIS_IRIS_CONTEXT_RETRIEVER_MCP_ENDPOINT")!),
            document.RootElement.GetProperty("contextRetrieverAgentKey").GetString()!,
            document.RootElement.GetProperty("contextRetrieverCredentialVersion").GetString()!,
            users);
    }

    public Task<IContextRetrieverClient> ConnectAsync(ContextLiveUser user) => new ContextRetrieverClientFactory().ConnectAsync(
        new ContextRetrieverClientOptions { Endpoint = Endpoint }, AgentKey, user.DelegatedToken);
}

internal sealed class ContextLiveFactAttribute : FactAttribute
{
    public ContextLiveFactAttribute()
    {
        var endpoint = Environment.GetEnvironmentVariable("REDIS_IRIS_CONTEXT_RETRIEVER_MCP_ENDPOINT");
        var usersFile = ResolveUsersFile();
        if (string.IsNullOrWhiteSpace(endpoint) || !Uri.TryCreate(endpoint, UriKind.Absolute, out _) || !File.Exists(usersFile))
            Skip = "Set REDIS_IRIS_CONTEXT_RETRIEVER_MCP_ENDPOINT and provision the scoped Context Retriever users file to run live tests.";
    }

    internal static string ResolveUsersFile()
    {
        var configured = Environment.GetEnvironmentVariable("REDIS_IRIS_CONTEXT_RETRIEVER_USERS_FILE");
        if (!string.IsNullOrWhiteSpace(configured)) return Path.GetFullPath(configured);

        const string relative = "artifacts/context-retriever/redis-iris-travel-users.json";
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory }.Distinct(StringComparer.Ordinal))
        {
            for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
            {
                var candidate = Path.Combine(directory.FullName, relative);
                if (File.Exists(candidate)) return candidate;
            }
        }

        return Path.GetFullPath(relative);
    }
}
