// Copyright (c) Redis, Inc.

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Redis.Iris.AgentMemory;

namespace Microsoft.Agents.AI.Redis.Iris;

/// <summary>Additional server-side constraints for owner-scoped recall.</summary>
public sealed record RedisIrisMemorySearchOptions
{
    /// <summary>Gets a session filter.</summary>
    public StringFilter? SessionId { get; init; }
    /// <summary>Gets a deprecated namespace-label filter.</summary>
    public StringFilter? Namespace { get; init; }
    /// <summary>Gets a stable namespace-resource filter.</summary>
    public StringFilter? NamespaceRef { get; init; }
    /// <summary>Gets whether only unnamespaced memories match.</summary>
    public bool? Unnamespaced { get; init; }
    /// <summary>Gets a topic filter.</summary>
    public StringFilter? Topics { get; init; }
    /// <summary>Gets a memory-type filter.</summary>
    public StringFilter? MemoryType { get; init; }
    /// <summary>Gets a creation-time filter.</summary>
    public DateTimeFilter? CreatedAt { get; init; }
    /// <summary>Gets the minimum normalized cosine similarity.</summary>
    public double? SimilarityThreshold { get; init; }
    /// <summary>Gets the maximum number of results.</summary>
    public int Limit { get; init; } = 10;
    /// <summary>Gets an opaque continuation token.</summary>
    public string? PageToken { get; init; }
}

/// <summary>Executes long-term-memory searches that cannot omit or replace owner scope.</summary>
public sealed class RedisIrisLongTermMemorySearch
{
    private readonly IRedisAgentMemoryClient _client;

    /// <summary>Initializes the owner-scoped search service.</summary>
    public RedisIrisLongTermMemorySearch(IRedisAgentMemoryClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        this._client = client;
    }

    /// <summary>Searches with the latest non-empty external user message.</summary>
    public Task<LongTermMemorySearchResult> SearchAsync(
        string ownerId,
        IEnumerable<ChatMessage> messages,
        RedisIrisMemorySearchOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        ArgumentNullException.ThrowIfNull(messages);
        var text = messages
            .Where(static message => message.Role == ChatRole.User && message.GetAgentRequestMessageSourceType() == AgentRequestMessageSourceType.External)
            .Select(static message => message.Text)
            .LastOrDefault(static value => !string.IsNullOrWhiteSpace(value));
        return this.SearchAsync(ownerId, text, options, cancellationToken);
    }

    /// <summary>Searches with an explicit query and mandatory owner equality filter.</summary>
    public Task<LongTermMemorySearchResult> SearchAsync(
        string ownerId,
        string? text,
        RedisIrisMemorySearchOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        options ??= new RedisIrisMemorySearchOptions();
        if (options.Limit is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Limit must be between 1 and 100.");
        }

        return this._client.SearchLongTermMemoryAsync(new LongTermMemorySearchRequest
        {
            Text = text,
            SimilarityThreshold = options.SimilarityThreshold,
            Limit = options.Limit,
            PageToken = options.PageToken,
            FilterOp = "all",
            Filter = new LongTermMemoryFilter
            {
                OwnerId = new StringFilter { Eq = ownerId },
                SessionId = options.SessionId,
                Namespace = options.Namespace,
                NamespaceRef = options.NamespaceRef,
                Unnamespaced = options.Unnamespaced,
                Topics = options.Topics,
                MemoryType = options.MemoryType,
                CreatedAt = options.CreatedAt,
            },
        }, cancellationToken);
    }
}
