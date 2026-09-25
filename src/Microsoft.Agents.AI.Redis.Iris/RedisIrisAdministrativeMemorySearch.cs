// Copyright (c) Redis, Inc.

using Redis.Iris.AgentMemory;

namespace Microsoft.Agents.AI.Redis.Iris;

/// <summary>A deliberately privileged cross-owner search request.</summary>
public sealed record RedisIrisAdministrativeSearchRequest(
    string Reason,
    string? Text,
    LongTermMemoryFilter Filter,
    int Limit = 10,
    string? PageToken = null);

/// <summary>Provides explicitly authorized administrative memory search.</summary>
public sealed class RedisIrisAdministrativeMemorySearch
{
    private readonly IRedisAgentMemoryClient _client;
    private readonly Func<RedisIrisAdministrativeSearchRequest, CancellationToken, ValueTask<bool>> _authorize;

    /// <summary>Initializes the privileged API with a mandatory host authorization callback.</summary>
    public RedisIrisAdministrativeMemorySearch(
        IRedisAgentMemoryClient client,
        Func<RedisIrisAdministrativeSearchRequest, CancellationToken, ValueTask<bool>> authorize)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(authorize);
        this._client = client;
        this._authorize = authorize;
    }

    /// <summary>Searches across owner boundaries only after explicit host authorization.</summary>
    public async Task<LongTermMemorySearchResult> SearchAcrossOwnersAsync(
        RedisIrisAdministrativeSearchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Reason))
        {
            throw new ArgumentException("An administrative reason is required.", nameof(request));
        }

        if (!await this._authorize(request, cancellationToken).ConfigureAwait(false))
        {
            throw new UnauthorizedAccessException("Administrative cross-owner memory search was denied.");
        }

        return await this._client.SearchLongTermMemoryAsync(new()
        {
            Text = request.Text,
            Filter = request.Filter,
            FilterOp = "all",
            Limit = request.Limit,
            PageToken = request.PageToken,
        }, cancellationToken).ConfigureAwait(false);
    }
}
