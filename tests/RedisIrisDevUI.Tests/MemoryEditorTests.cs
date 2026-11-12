using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Redis.Iris.AgentFramework;
using Redis.Iris.AgentMemory;
using Xunit;

namespace RedisIrisDevUI.Tests;

public sealed class MemoryEditorTests
{
    [Fact]
    public async Task BrowserApiRequiresAntiforgeryAndSavesExactlyThePreparedProposal()
    {
        var settings = new SampleOptions(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["REDIS_IRIS_ENDPOINT"] = "http://localhost:9000",
            ["REDIS_IRIS_STORE_ID"] = "test",
            ["REDIS_IRIS_API_KEY"] = "test",
            ["REDIS_IRIS_API_MODE"] = "Iris",
            ["OPENAI_API_KEY"] = "test",
            ["REDIS_IRIS_CONTEXT_RETRIEVER_MCP_ENDPOINT"] = "http://localhost:8081",
            ["REDIS_IRIS_CONTEXT_RETRIEVER_AGENT_KEY"] = "test",
        }).Build());
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0"); builder.Logging.ClearProviders();
        builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
        builder.Services.AddAntiforgery(options => options.HeaderName = "X-CSRF-TOKEN");
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddSingleton(provider => new TravelUserAccessor(provider.GetRequiredService<IHttpContextAccessor>(),
            TravelUserDirectory.Parse(null), settings.CreateLegacyUser(), TravelAuthenticationMode.Token));
        builder.Services.AddSingleton<MemoryProposalStore>();
        var client = new TestMemoryClient();
        builder.Services.AddSingleton<IRedisAgentMemoryClient>(client);
        builder.Services.AddSingleton(provider =>
        {
            var users = provider.GetRequiredService<TravelUserAccessor>(); var proposals = provider.GetRequiredService<MemoryProposalStore>();
            return new RedisIrisLongTermMemoryWriter(client, new()
            {
                ApiMode = AgentMemoryApiMode.Iris,
                IdentityResolver = new RamIdentityResolver(users).ResolveMemoryWriteIdentity,
                AuthorizationPolicy = _ => true,
                MemoryWrites = new() { AuthorizeWriteAsync = (_, proposal, _) => ValueTask.FromResult(proposals.IsApproved(users.Current.IsolationKey, proposal)) },
            });
        });
        await using var app = builder.Build();
        var resolver = new RamIdentityResolver(app.Services.GetRequiredService<TravelUserAccessor>());
        Assert.Throws<ArgumentNullException>(() => resolver.Resolve(null));
        Assert.Equal("memory-editor", resolver.ResolveMemoryWriteIdentity(null).SessionId);
        app.MapGroup("").MapMemoryEditor(settings);
        await app.StartAsync();
        using var http = new HttpClient(new HttpClientHandler { UseCookies = true }) { BaseAddress = new(app.Urls.Single()) };
        const string api = "/api/ram/memory-editor";
        var draft = new { text = "window <script>alert(1)</script>", topics = new[] { "travel" } };
        Assert.Equal(HttpStatusCode.BadRequest, (await http.PostAsJsonAsync(api + "/proposals", draft)).StatusCode);
        Assert.Equal(0, client.Writes);
        var html = await http.GetStringAsync("/memories");
        var csrf = Regex.Match(html, "name=\"csrf-token\" content=\"([^\"]+)\"").Groups[1].Value;
        http.DefaultRequestHeaders.Add("X-CSRF-TOKEN", WebUtility.HtmlDecode(csrf));
        var prepared = await (await http.PostAsJsonAsync(api + "/proposals", draft)).Content.ReadFromJsonAsync<JsonElement>();
        var proposalId = prepared.GetProperty("id").GetString();
        Assert.Equal(0, client.Writes);
        Assert.Equal(draft.text, prepared.GetProperty("text").GetString());
        var url = api + "/proposals/" + proposalId + "/save";
        Assert.Equal(HttpStatusCode.OK, (await http.PostAsJsonAsync(url, new { })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await http.PostAsJsonAsync(url, new { })).StatusCode);
        Assert.Equal(1, client.Writes);
        Assert.Equal(draft.text, client.Record!.Text);
        Assert.Equal(HttpStatusCode.NotFound, (await http.PostAsJsonAsync(api + "/proposals/unknown/save", new { })).StatusCode);
        Assert.DoesNotContain(draft.text, html);
        var edit = new { memoryId = client.Record.Id, expectedRevision = "r1", text = "aisle", topics = Array.Empty<string>() };
        var editPrepared = await (await http.PostAsJsonAsync(api + "/proposals", edit)).Content.ReadFromJsonAsync<JsonElement>();
        client.Record = client.Record with { Revision = "r2", Text = "concurrent change" };
        var conflict = await http.PostAsJsonAsync(api + "/proposals/" + editPrepared.GetProperty("id").GetString() + "/save", new { });
        Assert.Equal(HttpStatusCode.PreconditionFailed, conflict.StatusCode);
        Assert.Equal("concurrent change", client.Record.Text);
        await app.StopAsync();
    }

    [Fact]
    public async Task ProposalStoreHidesOtherUsersAndSerializesDuplicateSaves()
    {
        var store = new MemoryProposalStore(); var client = new TestMemoryClient();
        var writer = new RedisIrisLongTermMemoryWriter(client, new()
        {
            ApiMode = AgentMemoryApiMode.Iris,
            IdentityResolver = _ => new("tenant", "owner", "session", "actor", "agent"),
            AuthorizationPolicy = _ => true,
            MemoryWrites = new() { AuthorizeWriteAsync = (_, proposal, _) => ValueTask.FromResult(store.IsApproved("alice", proposal)) },
        });
        var proposal = writer.PrepareCreate("fact"); store.Add("alice", proposal, null);
        Assert.Empty(store.List("bob")); Assert.False(store.IsApproved("alice", proposal));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => store.SaveAsync("bob", proposal.Id, writer, default));
        await Task.WhenAll(store.SaveAsync("alice", proposal.Id, writer, default), store.SaveAsync("alice", proposal.Id, writer, default));
        Assert.Equal(1, client.Writes); Assert.Empty(store.List("alice")); Assert.False(store.IsApproved("alice", proposal));
    }

    [Fact]
    public void EditorEscapesTokenAndShowsUnsupportedMode()
    {
        var page = MemoryEditorPage.Render("\"><script>bad</script>", false);
        Assert.DoesNotContain("<script>bad</script>", page);
        Assert.Contains("data-enabled=\"false\"", page);
    }

    private sealed class TestMemoryClient : IRedisAgentMemoryClient
    {
        public LongTermMemoryRecord? Record;
        public int Writes;
        public Task<LongTermMemoryRecord> GetLongTermMemoryAsync(string memoryId, CancellationToken cancellationToken = default) => Task.FromResult(Record!);
        public Task<LongTermMemorySearchResult> SearchLongTermMemoryAsync(LongTermMemorySearchRequest request, CancellationToken cancellationToken = default) => Task.FromResult(new LongTermMemorySearchResult(Record is null ? [] : [Record]));
        public Task<BulkCreateLongTermMemoriesResult> BulkCreateLongTermMemoriesAsync(BulkCreateLongTermMemoriesRequest request, CancellationToken cancellationToken = default)
        {
            Writes++; var item = request.Memories.Single();
            Record = new(item.Id, item.Text, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, item.MemoryType, item.SessionId, item.OwnerId, Topics: item.Topics) { Revision = "r1" };
            return Task.FromResult(new BulkCreateLongTermMemoriesResult([item.Id]));
        }
        public Task<LongTermMemoryRecord> UpdateLongTermMemoryAsync(string memoryId, UpdateLongTermMemoryRequest request, CancellationToken cancellationToken = default)
        {
            if (request.ExpectedRevision != Record!.Revision) throw new AgentMemoryException(HttpStatusCode.PreconditionFailed, "update");
            Writes++; Record = Record with { Text = request.Text!, Revision = "r2" }; return Task.FromResult(Record);
        }
        public Task<SessionEvent> AddSessionEventAsync(AddSessionEventRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SessionMemory> GetSessionMemoryAsync(string sessionId, bool includeSummarizedEvents = false, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SessionEvent> GetSessionEventAsync(string sessionId, string eventId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteSessionEventAsync(string sessionId, string eventId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteSessionMemoryAsync(string sessionId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
