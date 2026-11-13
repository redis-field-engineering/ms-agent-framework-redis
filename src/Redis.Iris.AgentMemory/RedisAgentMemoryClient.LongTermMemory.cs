// Copyright (c) Redis, Inc.

using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Redis.Iris.AgentMemory;

public sealed partial class RedisAgentMemoryClient
{
    /// <inheritdoc />
    public async Task<BulkCreateLongTermMemoriesResult> BulkCreateLongTermMemoriesAsync(
        BulkCreateLongTermMemoriesRequest request, CancellationToken cancellationToken = default)
    {
        RequireIris();
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Memories);
        if (request.Memories.Count is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(request), "Supply 1-100 memories.");
        ValidateReplay(request.IdempotencyKey, null);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var memory in request.Memories)
        {
            ArgumentNullException.ThrowIfNull(memory);
            ValidateMemoryId(memory.Id);
            if (!ids.Add(memory.Id)) throw new ArgumentException("Memory IDs must be distinct within a batch.", nameof(request));
            ValidateContent(memory);
        }
        var body = new
        {
            request.IdempotencyKey,
            Memories = request.Memories.Select(memory => new
            {
                MemoryId = memory.Id,
                memory.Text,
                memory.OwnerId,
                memory.MemoryType,
                memory.SessionId,
                NamespaceId = memory.NamespaceRef?.Id,
                memory.Topics,
                memory.Attributes,
            }).ToArray(),
        };
        var response = await SendMemoryAsync<BulkCreateResponse>(HttpMethod.Post, _memoryPath, body,
            "ltm.create", cancellationToken, result => result.Errors?.Count > 0,
            result => { if (result.Created is null) throw new InvalidDataException("Agent Memory returned no created IDs."); }).ConfigureAwait(false);
        return new(response.Created, response.Errors?.Select(error => error.ToPublic()).ToArray());
    }

    /// <inheritdoc />
    public async Task<LongTermMemoryRecord> GetLongTermMemoryAsync(string memoryId, CancellationToken cancellationToken = default)
    {
        RequireIris();
        ValidateMemoryId(memoryId);
        var result = await SendMemoryAsync<IrisAgentMemoryContract.MemoryRecord>(HttpMethod.Get,
            $"{_memoryPath}/{Uri.EscapeDataString(memoryId)}", null, "ltm.get", cancellationToken, validateResponse: value => ReadMemory(value, memoryId)).ConfigureAwait(false);
        return ReadMemory(result, memoryId);
    }

    /// <inheritdoc />
    public async Task<LongTermMemoryRecord> UpdateLongTermMemoryAsync(
        string memoryId, UpdateLongTermMemoryRequest request, CancellationToken cancellationToken = default)
    {
        RequireIris();
        ValidateMemoryId(memoryId);
        ArgumentNullException.ThrowIfNull(request);
        ValidateReplay(request.IdempotencyKey, request.ExpectedRevision);
        ValidateFields(request.Text, request.MemoryType, request.OwnerId, request.SessionId, request.NamespaceRef, request.Topics, allowEmptySession: true);
        if (request.Text is null && request.MemoryType is null && request.OwnerId is null && request.SessionId is null && request.NamespaceRef is null && request.Topics is null)
            throw new ArgumentException("Supply at least one field to update.", nameof(request));
        var body = new
        {
            request.Text,
            request.MemoryType,
            request.Topics,
            request.OwnerId,
            request.SessionId,
            NamespaceId = request.NamespaceRef?.Id,
            request.ExpectedRevision,
            request.IdempotencyKey,
        };
        var result = await SendMemoryAsync<IrisAgentMemoryContract.MemoryRecord>(HttpMethod.Patch,
            $"{_memoryPath}/{Uri.EscapeDataString(memoryId)}", body, "ltm.update", cancellationToken, validateResponse: value => ReadMemory(value, memoryId)).ConfigureAwait(false);
        return ReadMemory(result, memoryId);
    }

    /// <inheritdoc />
    public async Task<UpsertLongTermMemoryResult> UpsertLongTermMemoryAsync(
        string memoryId, UpsertLongTermMemoryRequest request, CancellationToken cancellationToken = default)
    {
        RequireIris();
        ValidateMemoryId(memoryId);
        ArgumentNullException.ThrowIfNull(request);
        ValidateContent(request);
        ValidateReplay(request.IdempotencyKey, request.ExpectedRevision);
        var body = new
        {
            request.Text,
            request.OwnerId,
            request.MemoryType,
            request.SessionId,
            NamespaceId = request.NamespaceRef?.Id,
            request.Topics,
            request.Attributes,
            request.ExpectedRevision,
            request.IdempotencyKey,
        };
        var result = await SendMemoryAsync<UpsertResponse>(HttpMethod.Put,
            $"{_memoryPath}/{Uri.EscapeDataString(memoryId)}", body, "ltm.upsert", cancellationToken, validateResponse: value =>
            {
                if (value.MemoryId != memoryId || value.Created is null)
                    throw new InvalidDataException("Agent Memory returned an invalid upsert result.");
            }).ConfigureAwait(false);
        return new(result.MemoryId, result.Created!.Value);
    }

    private void RequireIris()
    {
        if (!_useIrisApi) throw new NotSupportedException("Direct memory creation, reads, and updates require AgentMemoryApiMode.Iris.");
    }

    private static LongTermMemoryRecord ReadMemory(IrisAgentMemoryContract.MemoryRecord result, string expectedId)
    {
        if (result.MemoryId != expectedId || result.Text is null || result.CreatedAt == default || result.UpdatedAt == default)
            throw new InvalidDataException("Agent Memory returned an invalid memory record.");
        return result.ToPublic();
    }

    private async Task<T> SendMemoryAsync<T>(HttpMethod method, string path, object? body, string operation,
        CancellationToken cancellationToken, Func<T, bool>? partialFailure = null, Action<T>? validateResponse = null)
    {
        using var activity = AgentMemoryDiagnostics.StartActivity(operation, _endpoint);
        var start = AgentMemoryDiagnostics.StartRequest(operation);
        try
        {
            using var message = await CreateRequestAsync(method, path, cancellationToken).ConfigureAwait(false);
            if (body is not null) message.Content = JsonContent.Create(body, options: JsonOptions);
            using var response = await _httpClient.SendAsync(message, cancellationToken).ConfigureAwait(false);
            await EnsureSuccessAsync(response, operation, cancellationToken).ConfigureAwait(false);
            var result = await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("Agent Memory returned no result.");
            validateResponse?.Invoke(result);
            if (partialFailure?.Invoke(result) == true)
            {
                AgentMemoryDiagnostics.RecordPartialSuccess(activity, operation, start, (int)response.StatusCode);
            }
            else
            {
                AgentMemoryDiagnostics.SetSuccess(activity);
                activity?.SetTag("http.response.status_code", (int)response.StatusCode);
                AgentMemoryDiagnostics.RecordMetricsSuccess(operation, start, 0);
            }
            return result;
        }
        catch (Exception exception)
        {
            AgentMemoryDiagnostics.SetError(activity, exception);
            AgentMemoryDiagnostics.RecordMetricsError(operation, start);
            throw;
        }
    }

    private static void ValidateMemoryId(string id) => ValidatePattern(id, "memoryId", 64, "^[A-Za-z0-9_-]+$");

    private static void ValidateContent(LongTermMemoryContent content)
    {
        ArgumentNullException.ThrowIfNull(content.Text);
        ArgumentNullException.ThrowIfNull(content.OwnerId);
        ValidateFields(content.Text, content.MemoryType, content.OwnerId, content.SessionId, content.NamespaceRef, content.Topics);
        if (content.Attributes?.Values.Any(value => value.ValueKind == JsonValueKind.Undefined) == true)
            throw new ArgumentException("Attributes must contain valid JSON values.", nameof(content));
    }

    private static void ValidateFields(string? text, string? type, string? owner, string? session,
        NamespaceReferenceInput? ns, IReadOnlyList<string>? topics, bool allowEmptySession = false)
    {
        if (text is not null && (string.IsNullOrWhiteSpace(text) || text.Length > 50_000))
            throw new ArgumentException("Text must contain 1-50000 characters and cannot be blank.", nameof(text));
        if (type is not null) ValidatePattern(type, "memoryType", 64, "^[a-zA-Z][a-zA-Z0-9_-]*$");
        if (owner is not null) ValidatePattern(owner, "ownerId", 64, @"^(?:[\w+=@-][\w+=.@-]*|\.[\w+=@-][\w+=.@-]*|\.\.[\w+=.@-]+)$");
        if (session is not null && !(allowEmptySession && session.Length == 0)) ValidatePattern(session, "sessionId", 64, "^[A-Za-z0-9_-]+$");
        if (ns is not null) ValidatePattern(ns.Id, "namespaceId", 35, "^ns_[0-9a-f]{32}$");
        if (topics is not null && (topics.Count > 50 || topics.Any(topic => string.IsNullOrWhiteSpace(topic) || topic.Length > 100)))
            throw new ArgumentException("Supply at most 50 nonblank topics of 1-100 characters.", nameof(topics));
    }

    private static void ValidateReplay(string? key, string? revision)
    {
        if (key is not null) ValidatePattern(key, "idempotencyKey", 128, "^[ -~]+$");
        if (revision is not null && (string.IsNullOrWhiteSpace(revision) || revision.Length > 64))
            throw new ArgumentException("ExpectedRevision must contain 1-64 characters.", nameof(revision));
    }

    private static void ValidatePattern(string value, string name, int max, string pattern)
    {
        if (string.IsNullOrEmpty(value) || value.Length > max || !Regex.IsMatch(value, pattern.TrimEnd('$') + @"\z", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
            throw new ArgumentException($"Invalid {name} format or length (maximum {max}).", name);
    }

    private sealed record BulkCreateResponse(IReadOnlyList<string> Created, IReadOnlyList<IrisAgentMemoryContract.BulkError>? Errors = null);
    private sealed record UpsertResponse(string MemoryId, bool? Created);
}
