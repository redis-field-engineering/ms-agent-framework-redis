using System.Net;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Redis.Iris.AgentMemory;

namespace Redis.Iris.AgentFramework.Tests;

public sealed class RedisIrisLongTermMemoryWriterTests
{
    [Fact]
    public async Task AttributeCorrectionSearchRetainsIdentityAndRejectsForeignResults()
    {
        var client = new MemoryClient();
        var writer = new RedisIrisLongTermMemoryWriter(client, Options());
        var options = new RedisIrisMemoryCorrectionSearchOptions
        {
            MemoryType = new() { Eq = "trip" },
            Attributes = new Dictionary<string, AttributeFilterClause> { ["city"] = AttributeFilterClause.StringEquals("Tokyo") },
            PageToken = "next",
            Limit = 3,
        };
        await writer.SearchWithOptionsAsync(options);
        var request = client.SearchRequest!;
        Assert.Equal("owner", request.Filter!.OwnerId!.Eq);
        Assert.Equal(["required"], request.Filter.Topics!.All);
        Assert.Equal("all", request.FilterOp);
        Assert.Equal("trip", request.Filter.MemoryType!.Eq);
        Assert.Same(options.Attributes, request.Filter.Attributes);
        Assert.Equal(3, request.Limit);
        Assert.Equal("next", request.PageToken);
        client.Record = client.Record with { OwnerId = "another-owner" };
        var error = await Assert.ThrowsAsync<AgentMemoryException>(() => writer.SearchWithOptionsAsync(options));
        Assert.Equal(HttpStatusCode.NotFound, error.StatusCode);
    }

    [Fact]
    public async Task WritesDenyByDefaultAndContentPolicyRunsBeforeDispatch()
    {
        var client = new MemoryClient(); var options = Options(); options.MemoryWrites.AuthorizeWriteAsync = null;
        var writer = new RedisIrisLongTermMemoryWriter(client, options);
        var proposal = writer.PrepareCreate("window seats");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => writer.ExecuteAsync(proposal));
        Assert.Equal(0, client.Writes);
        options.MemoryWrites.AuthorizeWriteAsync = (_, _, _) => ValueTask.FromResult(true);
        options.MemoryWrites.ValidateContentAsync = (_, _) => throw new ArgumentException("content rejected");
        await Assert.ThrowsAsync<ArgumentException>(() => writer.ExecuteAsync(proposal));
        Assert.Equal(0, client.Writes);
    }

    [Fact]
    public async Task CreationBindsScopeAndCopiesMutableInputs()
    {
        var client = new MemoryClient(); var writer = new RedisIrisLongTermMemoryWriter(client, Options());
        var topics = new[] { "travel" }; var attributes = new Dictionary<string, JsonElement> { ["seat"] = JsonSerializer.SerializeToElement("window") };
        var proposal = writer.PrepareCreate("window seats", topics, attributes: attributes);
        topics[0] = "injected"; attributes.Clear();
        var result = await writer.ExecuteAsync(proposal);
        Assert.Equal(proposal.MemoryId, result.MemoryId);
        var request = Assert.Single(client.CreateRequest!.Memories);
        Assert.Equal("owner", request.OwnerId); Assert.Equal("session", request.SessionId);
        Assert.Equal(["travel", "required"], request.Topics);
        Assert.Equal("window", request.Attributes!["seat"].GetString());
        Assert.Equal(proposal.Id, client.CreateRequest.IdempotencyKey);
    }

    [Fact]
    public async Task UpdatePreservesSessionAndRequiresReviewedRevision()
    {
        var client = new MemoryClient(); var writer = new RedisIrisLongTermMemoryWriter(client, Options());
        var proposal = await writer.PrepareUpdateAsync("memory", "r1", "aisle", []);
        var result = await writer.ExecuteAsync(proposal);
        Assert.Equal("r2", result.Memory!.Revision);
        Assert.Equal("original-session", client.UpdateRequest!.SessionId);
        Assert.Equal("r1", client.UpdateRequest.ExpectedRevision);
        Assert.Equal(["required"], client.UpdateRequest.Topics);
        Assert.Null(client.UpdateRequest.OwnerId);
        Assert.Null(client.UpdateRequest.NamespaceRef);
        await Assert.ThrowsAsync<AgentMemoryException>(() => writer.PrepareUpdateAsync("memory", "r1", "stale"));
    }

    [Fact]
    public async Task TwoEditorsCannotSilentlyOverwriteEachOther()
    {
        var client = new MemoryClient(); var writer = new RedisIrisLongTermMemoryWriter(client, Options());
        var first = await writer.PrepareUpdateAsync("memory", "r1", "first");
        var second = await writer.PrepareUpdateAsync("memory", "r1", "second");
        await writer.ExecuteAsync(first);
        var error = await Assert.ThrowsAsync<AgentMemoryException>(() => writer.ExecuteAsync(second));
        Assert.Equal(HttpStatusCode.PreconditionFailed, error.StatusCode);
        Assert.Equal("first", client.Record.Text);
        // Exact retry of the first request replays successfully, retaining its original revision.
        await writer.ExecuteAsync(first);
        Assert.Equal("r1", client.UpdateRequest!.ExpectedRevision);
    }

    [Fact]
    public async Task ReplacementCarriesAllUntouchedFieldsAndDoesNotCreateOnMissingTarget()
    {
        var client = new MemoryClient(); var writer = new RedisIrisLongTermMemoryWriter(client, Options());
        var proposal = await writer.PrepareUpdateAsync("memory", "r1", "aisle", attributes: new Dictionary<string, JsonElement>());
        await writer.ExecuteAsync(proposal);
        Assert.Equal("custom_type", client.UpsertRequest!.MemoryType);
        Assert.Equal("original-session", client.UpsertRequest.SessionId);
        Assert.Equal("owner", client.UpsertRequest.OwnerId);
        Assert.Equal("r1", client.UpsertRequest.ExpectedRevision);
        Assert.Equal(["required", "travel"], client.UpsertRequest.Topics);
        Assert.Empty(client.UpsertRequest.Attributes!);
        client.Missing = true;
        var writes = client.Writes;
        await Assert.ThrowsAsync<AgentMemoryException>(() => writer.ExecuteAsync(proposal));
        Assert.Equal(writes, client.Writes);
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("namespace")]
    [InlineData("topics")]
    public async Task OutOfScopeRecordsAreNeitherReturnedNorMutated(string changed)
    {
        var client = new MemoryClient(); var writer = new RedisIrisLongTermMemoryWriter(client, Options());
        var proposal = await writer.PrepareUpdateAsync("memory", "r1", "new");
        client.Record = changed switch
        {
            "owner" => client.Record with { OwnerId = "other" },
            "namespace" => client.Record with { NamespaceRef = new("other") },
            _ => client.Record with { Topics = [] },
        };
        foreach (var task in new Func<Task>[] { () => writer.GetAsync("memory"), () => writer.SearchAsync(), () => writer.ExecuteAsync(proposal) })
        {
            var error = await Assert.ThrowsAsync<AgentMemoryException>(task);
            Assert.Equal(HttpStatusCode.NotFound, error.StatusCode);
        }
        Assert.Equal(0, client.Writes);
    }

    [Fact]
    public async Task IdentityChangesInvalidateProposalsAndResumedSessions()
    {
        var client = new MemoryClient(); var options = Options(); var identity = Identity();
        options.IdentityResolver = _ => identity;
        var writer = new RedisIrisLongTermMemoryWriter(client, options); var session = new Session();
        var proposal = writer.PrepareCreate("fact", session: session);
        identity = identity with { OwnerId = "other" };
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => writer.ExecuteAsync(proposal, session));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => writer.GetAsync("memory", session));
        Assert.Equal(0, client.Writes);
    }

    [Fact]
    public async Task IdentityIsRecheckedAfterApprovalCallback()
    {
        var client = new MemoryClient(); var options = Options(); var identity = Identity();
        options.IdentityResolver = _ => identity;
        options.MemoryWrites.AuthorizeWriteAsync = (_, _, _) => { identity = identity with { TenantId = "other" }; return ValueTask.FromResult(true); };
        var writer = new RedisIrisLongTermMemoryWriter(client, options);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => writer.ExecuteAsync(writer.PrepareCreate("fact")));
        Assert.Equal(0, client.Writes);
    }

    [Fact]
    public async Task ProtectedOrUnversionedRecordsFailClosed()
    {
        var client = new MemoryClient(); var writer = new RedisIrisLongTermMemoryWriter(client, Options());
        client.Record = client.Record with { DocumentManaged = true };
        await Assert.ThrowsAsync<InvalidOperationException>(() => writer.PrepareUpdateAsync("memory", "r1", "new"));
        client.Record = client.Record with { DocumentManaged = false, Revision = null };
        await Assert.ThrowsAsync<NotSupportedException>(() => writer.PrepareUpdateAsync("memory", "r1", "new"));
        Assert.Equal(0, client.Writes);
    }

    [Fact]
    public async Task CommittedCreateWithFailedReadReturnsRefreshPending()
    {
        var client = new MemoryClient { FailReadAfterWrite = true }; var writer = new RedisIrisLongTermMemoryWriter(client, Options());
        var result = await writer.ExecuteAsync(writer.PrepareCreate("fact"));
        Assert.True(result.RefreshPending); Assert.Null(result.Memory); Assert.Equal(1, client.Writes);
    }

    [Fact]
    public async Task ToolsPrepareOnlyAndRequireAnObservedUpdateTarget()
    {
        var client = new MemoryClient(); var writer = new RedisIrisLongTermMemoryWriter(client, Options());
        var proposals = new List<RedisIrisMemoryWriteProposal>();
        var tools = RedisIrisMemoryTools.Create(writer, new Session(), (proposal, _) => { proposals.Add(proposal); return Task.CompletedTask; });
        Assert.All(tools, tool =>
        {
            var schema = tool.JsonSchema.GetRawText();
            foreach (var forbidden in new[] { "ownerId", "namespaceId", "expectedRevision", "userApproved", "idempotencyKey" }) Assert.DoesNotContain(forbidden, schema);
        });
        var update = tools.Single(tool => tool.Name == "update_long_term_memory");
        await update.InvokeAsync(new AIFunctionArguments { ["memoryId"] = "memory", ["text"] = "new", ["topics"] = null });
        Assert.Empty(proposals);
        await tools.Single(tool => tool.Name == "find_long_term_memories").InvokeAsync(new AIFunctionArguments { ["query"] = "seat" });
        await update.InvokeAsync(new AIFunctionArguments { ["memoryId"] = "memory", ["text"] = "new", ["topics"] = null, ["userApproved"] = true });
        Assert.Single(proposals);
        Assert.Equal(0, client.Writes);
    }

    internal static RedisIrisIdentityContext Identity() => new("tenant", "owner", "session", "actor", "agent", Topics: ["required"]);
    internal static RedisIrisAgentMemoryOptions Options() => new()
    {
        ApiMode = AgentMemoryApiMode.Iris,
        IdentityResolver = _ => Identity(),
        AuthorizationPolicy = _ => true,
        MemoryWrites = new() { AuthorizeWriteAsync = (_, _, _) => ValueTask.FromResult(true) },
    };
    private sealed class Session : AgentSession;

    private sealed class MemoryClient : IRedisAgentMemoryClient
    {
        public LongTermMemoryRecord Record = new("memory", "window", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            "custom_type", "original-session", "owner", Topics: ["required", "travel"])
        { Revision = "r1" };
        public int Writes;
        public bool Missing;
        public bool FailReadAfterWrite;
        public BulkCreateLongTermMemoriesRequest? CreateRequest;
        public UpdateLongTermMemoryRequest? UpdateRequest;
        public UpsertLongTermMemoryRequest? UpsertRequest;
        private readonly HashSet<string> _keys = [];
        public Task<LongTermMemoryRecord> GetLongTermMemoryAsync(string id, CancellationToken cancellationToken = default)
        {
            if (Missing) throw new AgentMemoryException(HttpStatusCode.NotFound, "get");
            if (FailReadAfterWrite && Writes > 0) throw new HttpRequestException();
            return Task.FromResult(Record);
        }
        public LongTermMemorySearchRequest? SearchRequest { get; private set; }
        public Task<LongTermMemorySearchResult> SearchLongTermMemoryAsync(LongTermMemorySearchRequest request, CancellationToken cancellationToken = default)
        { SearchRequest = request; return Task.FromResult(new LongTermMemorySearchResult([Record])); }
        public Task<BulkCreateLongTermMemoriesResult> BulkCreateLongTermMemoriesAsync(BulkCreateLongTermMemoriesRequest request, CancellationToken cancellationToken = default)
        {
            CreateRequest = request; Writes++;
            var memory = request.Memories.Single();
            Record = new(memory.Id, memory.Text, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, memory.MemoryType, memory.SessionId, memory.OwnerId,
                memory.NamespaceRef is null ? null : new(memory.NamespaceRef.Id), memory.Topics, memory.Attributes)
            { Revision = "r1" };
            return Task.FromResult(new BulkCreateLongTermMemoriesResult([memory.Id]));
        }
        public Task<LongTermMemoryRecord> UpdateLongTermMemoryAsync(string id, UpdateLongTermMemoryRequest request, CancellationToken cancellationToken = default)
        {
            UpdateRequest = request;
            if (_keys.Contains(request.IdempotencyKey!)) return Task.FromResult(Record);
            if (request.ExpectedRevision != Record.Revision) throw new AgentMemoryException(HttpStatusCode.PreconditionFailed, "update");
            _keys.Add(request.IdempotencyKey!); Writes++;
            Record = Record with { Text = request.Text!, Topics = request.Topics, Revision = "r2" };
            return Task.FromResult(Record);
        }
        public Task<UpsertLongTermMemoryResult> UpsertLongTermMemoryAsync(string id, UpsertLongTermMemoryRequest request, CancellationToken cancellationToken = default)
        { UpsertRequest = request; Writes++; Record = Record with { Text = request.Text, Attributes = request.Attributes, Revision = "r2" }; return Task.FromResult(new UpsertLongTermMemoryResult(id, false)); }
        public Task<SessionEvent> AddSessionEventAsync(AddSessionEventRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SessionMemory> GetSessionMemoryAsync(string sessionId, bool includeSummarizedEvents = false, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SessionEvent> GetSessionEventAsync(string sessionId, string eventId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteSessionEventAsync(string sessionId, string eventId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteSessionMemoryAsync(string sessionId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
