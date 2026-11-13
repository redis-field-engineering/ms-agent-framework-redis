// Copyright (c) Redis, Inc.

using System.Collections.ObjectModel;
using System.Net;
using System.Text.Json;
using Microsoft.Agents.AI;
using Redis.Iris.AgentMemory;

namespace Redis.Iris.AgentFramework;

/// <summary>The kind of an explicitly authorized direct memory write.</summary>
public enum RedisIrisMemoryWriteKind
{
    /// <summary>Create a new record without overwriting an existing ID.</summary>
    Create,
    /// <summary>Update text and topics while preserving other fields.</summary>
    Update,
    /// <summary>Replace custom attributes while preserving other reviewed fields.</summary>
    Replace,
}

/// <summary>An immutable, identity-bound write prepared by the writer for host review.</summary>
public sealed class RedisIrisMemoryWriteProposal
{
    internal RedisIrisMemoryWriteProposal(RedisIrisIdentityContext identity, RedisIrisMemoryWriteKind kind,
        string memoryId, string text, string? memoryType, IReadOnlyList<string>? topics,
        IReadOnlyDictionary<string, JsonElement>? attributes, LongTermMemoryRecord? current)
    {
        Identity = identity with { Topics = Array.AsReadOnly((identity.Topics ?? []).ToArray()) };
        Kind = kind;
        MemoryId = memoryId;
        Text = text;
        MemoryType = memoryType;
        Topics = Array.AsReadOnly((topics ?? []).ToArray());
        Attributes = attributes is null ? null : new ReadOnlyDictionary<string, JsonElement>(
            attributes.ToDictionary(pair => pair.Key, pair => pair.Value.Clone(), StringComparer.Ordinal));
        ExpectedRevision = current?.Revision;
        SessionId = current is null ? identity.SessionId : current.SessionId;
        Id = Guid.NewGuid().ToString("N");
        ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(15);
    }

    internal RedisIrisIdentityContext Identity { get; }
    internal string? SessionId { get; }
    /// <summary>Gets the host-generated proposal ID, also used as the exact-request replay key.</summary>
    public string Id { get; }
    /// <summary>Gets the write kind.</summary>
    public RedisIrisMemoryWriteKind Kind { get; }
    /// <summary>Gets the target memory ID.</summary>
    public string MemoryId { get; }
    /// <summary>Gets the proposed text.</summary>
    public string Text { get; }
    /// <summary>Gets the memory type.</summary>
    public string? MemoryType { get; }
    /// <summary>Gets the proposed topics.</summary>
    public IReadOnlyList<string> Topics { get; }
    /// <summary>Gets the replacement attributes, if any.</summary>
    public IReadOnlyDictionary<string, JsonElement>? Attributes { get; }
    /// <summary>Gets the reviewed revision; null for creation.</summary>
    public string? ExpectedRevision { get; }
    /// <summary>Gets when this proposal stops accepting approvals and retries.</summary>
    public DateTimeOffset ExpiresAt { get; }
}

/// <summary>The persisted outcome, including an optional refreshed record.</summary>
public sealed record RedisIrisMemoryWriteResult(string MemoryId, LongTermMemoryRecord? Memory, bool RefreshPending = false);

/// <summary>Host policies for direct writes. Approval is denied unless explicitly configured.</summary>
public sealed class RedisIrisMemoryWriteOptions
{
    /// <summary>Gets or sets the trusted approval callback. Never derive approval from model arguments.</summary>
    public Func<RedisIrisIdentityContext, RedisIrisMemoryWriteProposal, CancellationToken, ValueTask<bool>>? AuthorizeWriteAsync { get; set; }
    /// <summary>Gets or sets optional content validation. Throw to reject before persistence.</summary>
    public Func<RedisIrisMemoryWriteProposal, CancellationToken, ValueTask>? ValidateContentAsync { get; set; }
}

/// <summary>Prepares and executes explicitly approved, owner/namespace/topic-scoped memory writes.</summary>
public sealed class RedisIrisLongTermMemoryWriter
{
    private readonly IRedisAgentMemoryClient _client;
    private readonly RedisIrisAgentMemoryOptions _options;
    private readonly ProviderSessionState<RedisIrisIdentityContext> _state;

    /// <summary>Creates a writer using the same trusted identity and authorization policies as recall.</summary>
    public RedisIrisLongTermMemoryWriter(IRedisAgentMemoryClient client, RedisIrisAgentMemoryOptions options)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _state = new ProviderSessionState<RedisIrisIdentityContext>(Resolve, nameof(RedisIrisLongTermMemoryWriter));
    }

    /// <summary>Reads a record only within the current identity's exact namespace and mandatory topics.</summary>
    public async Task<LongTermMemoryRecord> GetAsync(string memoryId, AgentSession? session = null, CancellationToken cancellationToken = default)
    {
        var identity = Current(session);
        var record = await _client.GetLongTermMemoryAsync(memoryId, cancellationToken).ConfigureAwait(false);
        CheckRecord(identity, record);
        return record;
    }

    /// <summary>Searches within the writer's exact owner, namespace, and required topic scope.</summary>
    public async Task<LongTermMemorySearchResult> SearchAsync(string? query = null, string? pageToken = null,
        AgentSession? session = null, CancellationToken cancellationToken = default)
    {
        var identity = Current(session);
        var result = await _client.SearchLongTermMemoryAsync(new()
        {
            Text = query,
            PageToken = pageToken,
            Limit = 20,
            Filter = new()
            {
                OwnerId = new() { Eq = identity.OwnerId },
                NamespaceRef = identity.NamespaceId is null ? null : new() { Eq = identity.NamespaceId },
                Unnamespaced = identity.NamespaceId is null ? true : null,
                Topics = identity.Topics is { Count: > 0 } ? new() { All = identity.Topics } : null,
            },
        }, cancellationToken).ConfigureAwait(false);
        foreach (var record in result.Items) CheckRecord(identity, record);
        return result;
    }

    /// <summary>Prepares a new memory without writing it. Keep the returned proposal for retries.</summary>
    public RedisIrisMemoryWriteProposal PrepareCreate(string text, IReadOnlyList<string>? topics = null,
        string? memoryType = null, IReadOnlyDictionary<string, JsonElement>? attributes = null, AgentSession? session = null)
    {
        var identity = Current(session);
        ValidateText(text);
        var scopedTopics = MergeTopics(identity, topics);
        return new(identity, RedisIrisMemoryWriteKind.Create, Guid.NewGuid().ToString("N"), text,
            memoryType ?? "semantic", scopedTopics, attributes, null);
    }

    /// <summary>Prepares a correction against the revision the caller reviewed. Attributes request a full replacement.</summary>
    public async Task<RedisIrisMemoryWriteProposal> PrepareUpdateAsync(string memoryId, string expectedRevision, string text,
        IReadOnlyList<string>? topics = null, IReadOnlyDictionary<string, JsonElement>? attributes = null,
        AgentSession? session = null, CancellationToken cancellationToken = default)
    {
        var identity = Current(session);
        var record = await _client.GetLongTermMemoryAsync(memoryId, cancellationToken).ConfigureAwait(false);
        CheckRecord(identity, record);
        CheckEditable(record);
        if (string.IsNullOrEmpty(expectedRevision) || record.Revision != expectedRevision)
            throw new AgentMemoryException(HttpStatusCode.PreconditionFailed, "ltm.prepare", "/errors/revision-conflict");
        ValidateText(text);
        return new(identity, attributes is null ? RedisIrisMemoryWriteKind.Update : RedisIrisMemoryWriteKind.Replace,
            record.Id, text, record.MemoryType, MergeTopics(identity, topics ?? record.Topics),
            attributes ?? record.Attributes, record);
    }

    /// <summary>Persists an immutable proposal after current identity, host approval, and content checks.</summary>
    public async Task<RedisIrisMemoryWriteResult> ExecuteAsync(RedisIrisMemoryWriteProposal proposal,
        AgentSession? session = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        cancellationToken.ThrowIfCancellationRequested();
        var identity = Current(session);
        if (!SameIdentity(identity, proposal.Identity) || proposal.ExpiresAt <= DateTimeOffset.UtcNow)
            throw new UnauthorizedAccessException("The memory proposal is expired or belongs to a different identity or session.");
        var authorize = _options.MemoryWrites.AuthorizeWriteAsync;
        if (authorize is null || !await authorize(identity, proposal, cancellationToken).ConfigureAwait(false))
            throw new UnauthorizedAccessException("The host has not approved this memory write.");
        if (_options.MemoryWrites.ValidateContentAsync is { } validate)
            await validate(proposal, cancellationToken).ConfigureAwait(false);
        // Revalidate identity after asynchronous host callbacks, before any persistence.
        if (!SameIdentity(identity, Current(session))) throw new UnauthorizedAccessException("Memory identity changed during approval.");
        if (proposal.Kind != RedisIrisMemoryWriteKind.Create)
        {
            var current = await _client.GetLongTermMemoryAsync(proposal.MemoryId, cancellationToken).ConfigureAwait(false);
            CheckRecord(identity, current);
            CheckEditable(current);
            // Send the ORIGINAL revision and body. Iris can replay a previously committed request.
        }
        var ns = identity.NamespaceId is null ? null : new NamespaceReferenceInput(identity.NamespaceId);
        if (proposal.Kind == RedisIrisMemoryWriteKind.Create)
        {
            var result = await _client.BulkCreateLongTermMemoriesAsync(new()
            {
                IdempotencyKey = proposal.Id,
                Memories = [new()
                {
                    Id = proposal.MemoryId, Text = proposal.Text, OwnerId = identity.OwnerId,
                    MemoryType = proposal.MemoryType, SessionId = proposal.SessionId, NamespaceRef = ns,
                    Topics = proposal.Topics, Attributes = proposal.Attributes,
                }],
            }, cancellationToken).ConfigureAwait(false);
            if (result.Errors is { Count: > 0 } || !result.Created.Contains(proposal.MemoryId, StringComparer.Ordinal))
            {
                var error = result.Errors?.FirstOrDefault();
                throw new AgentMemoryException((HttpStatusCode)(error?.Status ?? 502), "ltm.create", error?.Type);
            }
        }
        else if (proposal.Kind == RedisIrisMemoryWriteKind.Update)
        {
            var result = await _client.UpdateLongTermMemoryAsync(proposal.MemoryId, new()
            {
                Text = proposal.Text,
                Topics = proposal.Topics,
                SessionId = proposal.SessionId ?? "",
                ExpectedRevision = proposal.ExpectedRevision,
                IdempotencyKey = proposal.Id,
            }, cancellationToken).ConfigureAwait(false);
            CheckRecord(identity, result);
            return new(proposal.MemoryId, result);
        }
        else
        {
            await _client.UpsertLongTermMemoryAsync(proposal.MemoryId, new()
            {
                Text = proposal.Text,
                OwnerId = identity.OwnerId,
                MemoryType = proposal.MemoryType,
                SessionId = proposal.SessionId,
                NamespaceRef = ns,
                Topics = proposal.Topics,
                Attributes = proposal.Attributes,
                ExpectedRevision = proposal.ExpectedRevision,
                IdempotencyKey = proposal.Id,
            }, cancellationToken).ConfigureAwait(false);
        }
        try
        {
            var refreshed = await GetAsync(proposal.MemoryId, session, cancellationToken).ConfigureAwait(false);
            return new(proposal.MemoryId, refreshed);
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException or InvalidDataException or UnauthorizedAccessException)
        {
            // The write committed. A failed follow-up read must not be presented as a failed mutation.
            return new(proposal.MemoryId, null, RefreshPending: true);
        }
    }

    private RedisIrisIdentityContext Resolve(AgentSession? session)
    {
        if (_options.ApiMode != AgentMemoryApiMode.Iris) throw new NotSupportedException("Direct memory writes require Iris API mode.");
        var identity = _options.IdentityResolver?.Invoke(session);
        if (identity is null || string.IsNullOrWhiteSpace(identity.TenantId) || string.IsNullOrWhiteSpace(identity.OwnerId) ||
            string.IsNullOrWhiteSpace(identity.SessionId) || string.IsNullOrWhiteSpace(identity.ActorId) || string.IsNullOrWhiteSpace(identity.AgentId) ||
            identity.NamespaceId is not null && string.IsNullOrWhiteSpace(identity.NamespaceId) || identity.Topics?.Any(string.IsNullOrWhiteSpace) == true ||
            _options.AuthorizationPolicy?.Invoke(identity) != true)
            throw new UnauthorizedAccessException("The host denied the current memory identity.");
        return identity with { Topics = Array.AsReadOnly((identity.Topics ?? []).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray()) };
    }

    private RedisIrisIdentityContext Current(AgentSession? session)
    {
        var current = Resolve(session);
        if (session is not null && !SameIdentity(current, _state.GetOrInitializeState(session)))
            throw new UnauthorizedAccessException("The current identity does not match the memory session.");
        return current;
    }

    private static bool SameIdentity(RedisIrisIdentityContext a, RedisIrisIdentityContext b) =>
        a.TenantId == b.TenantId && a.OwnerId == b.OwnerId && a.SessionId == b.SessionId && a.ActorId == b.ActorId &&
        a.AgentId == b.AgentId && a.NamespaceId == b.NamespaceId && (a.Topics ?? []).SequenceEqual(b.Topics ?? []);

    private static void CheckRecord(RedisIrisIdentityContext identity, LongTermMemoryRecord record)
    {
        if (record.OwnerId != identity.OwnerId || record.NamespaceRef?.Id != identity.NamespaceId ||
            (identity.Topics ?? []).Except(record.Topics ?? [], StringComparer.Ordinal).Any())
            throw new AgentMemoryException(HttpStatusCode.NotFound, "ltm.authorize");
    }

    private static void CheckEditable(LongTermMemoryRecord record)
    {
        if (record.DocumentManaged == true) throw new InvalidOperationException("Document-managed memory content is read-only.");
        if (string.IsNullOrEmpty(record.Revision)) throw new NotSupportedException("This record has no revision; conditional editing is unavailable.");
    }

    private static IReadOnlyList<string> MergeTopics(RedisIrisIdentityContext identity, IReadOnlyList<string>? topics)
    {
        var result = (topics ?? []).Concat(identity.Topics ?? []).Distinct(StringComparer.Ordinal).ToArray();
        if (result.Length > 50 || result.Any(topic => string.IsNullOrWhiteSpace(topic) || topic.Length > 100))
            throw new ArgumentException("Supply at most 50 nonblank topics of 1-100 characters.", nameof(topics));
        return result;
    }

    private static void ValidateText(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 50_000) throw new ArgumentException("Supply 1-50000 characters of memory text.", nameof(text));
    }
}
