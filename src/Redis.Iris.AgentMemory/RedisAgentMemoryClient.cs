// Copyright (c) Redis, Inc.

using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Redis.Iris.AgentMemory;

/// <summary>An HTTP client for the Redis Agent Memory data plane.</summary>
public sealed class RedisAgentMemoryClient : IRedisAgentMemoryClient
{
    private const int MaximumMetadataBytes = 16 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _httpClient;
    private readonly IAgentMemoryCredentialProvider _credentialProvider;
    private readonly IAgentMemoryDelegatedTokenProvider? _delegatedTokenProvider;
    private readonly string _storePath;
    private readonly Uri _endpoint;

    /// <summary>Initializes a new client.</summary>
    public RedisAgentMemoryClient(
        HttpClient httpClient,
        RedisAgentMemoryClientOptions options,
        IAgentMemoryCredentialProvider credentialProvider)
        : this(httpClient, options, credentialProvider, null)
    {
    }

    /// <summary>Initializes a client with optional per-request delegated user authentication.</summary>
    public RedisAgentMemoryClient(
        HttpClient httpClient,
        RedisAgentMemoryClientOptions options,
        IAgentMemoryCredentialProvider credentialProvider,
        IAgentMemoryDelegatedTokenProvider? delegatedTokenProvider)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(credentialProvider);

        if (!options.Endpoint.IsAbsoluteUri || options.Endpoint.Scheme != Uri.UriSchemeHttps && options.Endpoint.Scheme != Uri.UriSchemeHttp)
        {
            throw new ArgumentException("Endpoint must be an absolute HTTP or HTTPS URI.", nameof(options));
        }

        ValidateIdentifier(options.StoreId, nameof(options.StoreId));

        this._httpClient = httpClient;
        this._credentialProvider = credentialProvider;
        this._delegatedTokenProvider = delegatedTokenProvider;
        this._endpoint = options.Endpoint;
        this._storePath = $"v1/stores/{Uri.EscapeDataString(options.StoreId)}";
        this._httpClient.BaseAddress ??= EnsureTrailingSlash(options.Endpoint);
    }

    /// <inheritdoc />
    public async Task<SessionEvent> AddSessionEventAsync(AddSessionEventRequest request, CancellationToken cancellationToken = default)
    {
        using var activity = AgentMemoryDiagnostics.StartActivity("session.write", this._endpoint);
        var metricStart = AgentMemoryDiagnostics.StartRequest("session.write");
        try
        {
            ArgumentNullException.ThrowIfNull(request);
            ValidateIdentifier(request.ActorId, nameof(request.ActorId), 255);
            if (request.SessionId is not null)
            {
                ValidateIdentifier(request.SessionId, nameof(request.SessionId), 255);
            }

            if (request.CreatedAt.Offset != TimeSpan.Zero)
            {
                throw new ArgumentException("CreatedAt must be UTC.", nameof(request));
            }

            if (request.Content.Count == 0 || request.Content.Any(part => string.IsNullOrWhiteSpace(part.Text)))
            {
                throw new ArgumentException("At least one non-empty text content part is required.", nameof(request));
            }

            if (request.Metadata is { } metadata && JsonSerializer.SerializeToUtf8Bytes(metadata, JsonOptions).Length > MaximumMetadataBytes)
            {
                throw new ArgumentException("Metadata must not exceed 16 KiB.", nameof(request));
            }

            using var message = await this.CreateRequestAsync(HttpMethod.Post, $"{this._storePath}/session-memory/events", cancellationToken).ConfigureAwait(false);
            message.Content = JsonContent.Create(request, options: JsonOptions);
            using var response = await this._httpClient.SendAsync(message, cancellationToken).ConfigureAwait(false);
            await EnsureSuccessAsync(response, "session.add_event", cancellationToken).ConfigureAwait(false);
            var content = await response.Content.ReadFromJsonAsync<AddSessionEventResponse>(JsonOptions, cancellationToken).ConfigureAwait(false);
            var result = content?.Event ?? throw new InvalidDataException("Agent Memory returned no event.");
            AgentMemoryDiagnostics.SetSuccess(activity, 1);
            AgentMemoryDiagnostics.RecordMetricsSuccess("session.write", metricStart, 1);
            return result;
        }
        catch (Exception exception)
        {
            AgentMemoryDiagnostics.SetError(activity, exception);
            AgentMemoryDiagnostics.RecordMetricsError("session.write", metricStart);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<SessionMemory> GetSessionMemoryAsync(string sessionId, bool includeSummarizedEvents = false, CancellationToken cancellationToken = default) =>
        await this.GetSessionMemoryCoreAsync(sessionId, includeSummarizedEvents, notFoundIsMiss: false, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("A required session load returned no result.");

    /// <inheritdoc />
    public Task<SessionMemory?> TryGetSessionMemoryAsync(string sessionId, bool includeSummarizedEvents = false, CancellationToken cancellationToken = default) =>
        this.GetSessionMemoryCoreAsync(sessionId, includeSummarizedEvents, notFoundIsMiss: true, cancellationToken);

    private async Task<SessionMemory?> GetSessionMemoryCoreAsync(
        string sessionId,
        bool includeSummarizedEvents,
        bool notFoundIsMiss,
        CancellationToken cancellationToken)
    {
        using var activity = AgentMemoryDiagnostics.StartActivity("session.load", this._endpoint);
        var metricStart = AgentMemoryDiagnostics.StartRequest("session.load");
        try
        {
            ValidateIdentifier(sessionId, nameof(sessionId), 255);
            var path = $"{this._storePath}/session-memory/{Uri.EscapeDataString(sessionId)}";
            if (includeSummarizedEvents)
            {
                path += "?includeSummarisedEvents=true";
            }

            var result = await this.GetAsync<SessionMemory>(path, "session.get", cancellationToken).ConfigureAwait(false);
            AgentMemoryDiagnostics.SetSuccess(activity, result.Events.Count);
            AgentMemoryDiagnostics.RecordMetricsSuccess("session.load", metricStart, result.Events.Count);
            return result;
        }
        catch (AgentMemoryException exception) when (notFoundIsMiss && exception.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            AgentMemoryDiagnostics.SetNotFound(activity);
            AgentMemoryDiagnostics.RecordMetricsNotFound("session.load", metricStart);
            return null;
        }
        catch (Exception exception)
        {
            AgentMemoryDiagnostics.SetError(activity, exception);
            AgentMemoryDiagnostics.RecordMetricsError("session.load", metricStart);
            throw;
        }
    }

    /// <inheritdoc />
    public Task<SessionEvent> GetSessionEventAsync(string sessionId, string eventId, CancellationToken cancellationToken = default)
    {
        ValidateIdentifier(sessionId, nameof(sessionId), 255);
        ValidateIdentifier(eventId, nameof(eventId), 255);
        return this.GetEventAsync(sessionId, eventId, cancellationToken);
    }

    /// <inheritdoc />
    public Task DeleteSessionEventAsync(string sessionId, string eventId, CancellationToken cancellationToken = default)
    {
        ValidateIdentifier(sessionId, nameof(sessionId), 255);
        ValidateIdentifier(eventId, nameof(eventId), 255);
        return this.DeleteAsync(
            $"{this._storePath}/session-memory/{Uri.EscapeDataString(sessionId)}/events/{Uri.EscapeDataString(eventId)}",
            "session.delete_event",
            cancellationToken);
    }

    /// <inheritdoc />
    public Task DeleteSessionMemoryAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        ValidateIdentifier(sessionId, nameof(sessionId), 255);
        return this.DeleteAsync(
            $"{this._storePath}/session-memory/{Uri.EscapeDataString(sessionId)}",
            "session.delete",
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<SessionListResult> ListSessionsAsync(
        string ownerId,
        int limit = 100,
        string? pageToken = null,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentifier(ownerId, nameof(ownerId), 64);
        if (limit is < 1 or > 1000)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), "Limit must be between 1 and 1000.");
        }

        var path = $"{this._storePath}/session-memory?filterOwnerId={Uri.EscapeDataString(ownerId)}&limit={limit}";
        if (pageToken is not null)
        {
            path += $"&pageToken={Uri.EscapeDataString(pageToken)}";
        }

        return this.GetAsync<SessionListResult>(path, "session.list", cancellationToken);
    }

    /// <inheritdoc />
    public async Task<LongTermMemorySearchResult> SearchLongTermMemoryAsync(LongTermMemorySearchRequest request, CancellationToken cancellationToken = default)
    {
        using var activity = AgentMemoryDiagnostics.StartActivity("ltm.search", this._endpoint);
        var metricStart = AgentMemoryDiagnostics.StartRequest("ltm.search");
        try
        {
            ArgumentNullException.ThrowIfNull(request);
            if (request.Limit is < 1 or > 100)
            {
                throw new ArgumentOutOfRangeException(nameof(request), "Limit must be between 1 and 100.");
            }

            if (request.SimilarityThreshold is < 0 or > 1)
            {
                throw new ArgumentOutOfRangeException(nameof(request), "SimilarityThreshold must be between 0 and 1.");
            }

            using var message = await this.CreateRequestAsync(HttpMethod.Post, $"{this._storePath}/long-term-memory/search", cancellationToken).ConfigureAwait(false);
            message.Content = JsonContent.Create(request, options: JsonOptions);
            using var response = await this._httpClient.SendAsync(message, cancellationToken).ConfigureAwait(false);
            await EnsureSuccessAsync(response, "ltm.search", cancellationToken).ConfigureAwait(false);
            var result = await response.Content.ReadFromJsonAsync<LongTermMemorySearchResult>(JsonOptions, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("Agent Memory returned no search result.");
            AgentMemoryDiagnostics.SetSuccess(activity, result.Items.Count);
            AgentMemoryDiagnostics.RecordMetricsSuccess("ltm.search", metricStart, result.Items.Count);
            return result;
        }
        catch (Exception exception)
        {
            AgentMemoryDiagnostics.SetError(activity, exception);
            AgentMemoryDiagnostics.RecordMetricsError("ltm.search", metricStart);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<BulkDeleteLongTermMemoriesResult> BulkDeleteLongTermMemoriesAsync(
        IReadOnlyList<string> memoryIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(memoryIds);
        if (memoryIds.Count is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(memoryIds), "Memory IDs must contain 1-100 items.");
        }

        foreach (var memoryId in memoryIds)
        {
            ValidateIdentifier(memoryId, nameof(memoryIds), 64);
        }

        using var message = await this.CreateRequestAsync(HttpMethod.Delete, $"{this._storePath}/long-term-memory", cancellationToken).ConfigureAwait(false);
        message.Content = JsonContent.Create(new { memoryIds }, options: JsonOptions);
        using var response = await this._httpClient.SendAsync(message, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, "ltm.bulk_delete", cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<BulkDeleteLongTermMemoriesResult>(JsonOptions, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("Agent Memory returned no bulk delete result.");
    }

    private async Task<SessionEvent> GetEventAsync(string sessionId, string eventId, CancellationToken cancellationToken)
    {
        var wrapper = await this.GetAsync<GetSessionEventResponse>(
            $"{this._storePath}/session-memory/{Uri.EscapeDataString(sessionId)}/events/{Uri.EscapeDataString(eventId)}",
            "session.get_event",
            cancellationToken).ConfigureAwait(false);
        return wrapper.Event;
    }

    private async Task<T> GetAsync<T>(string path, string operation, CancellationToken cancellationToken)
    {
        using var message = await this.CreateRequestAsync(HttpMethod.Get, path, cancellationToken).ConfigureAwait(false);
        using var response = await this._httpClient.SendAsync(message, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, operation, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("Agent Memory returned an empty response.");
    }

    private async Task DeleteAsync(string path, string operation, CancellationToken cancellationToken)
    {
        using var message = await this.CreateRequestAsync(HttpMethod.Delete, path, cancellationToken).ConfigureAwait(false);
        using var response = await this._httpClient.SendAsync(message, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, operation, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<HttpRequestMessage> CreateRequestAsync(HttpMethod method, string path, CancellationToken cancellationToken)
    {
        var credential = await this._credentialProvider.GetCredentialAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(credential))
        {
            throw new InvalidOperationException("The Agent Memory credential provider returned an empty credential.");
        }

        AgentMemoryDelegatedToken? delegatedToken = null;
        if (this._delegatedTokenProvider is not null)
        {
            delegatedToken = await this._delegatedTokenProvider.GetTokenAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new UnauthorizedAccessException("The Agent Memory delegated token provider returned no token.");
            delegatedToken.Validate();
        }

        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        if (delegatedToken is not null)
        {
            request.Headers.Add("X-Iris-User-Token", delegatedToken.Token);
        }
        return request;
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, string operation, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        string? errorCode = null;
        try
        {
            using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), cancellationToken: cancellationToken).ConfigureAwait(false);
            if (body.RootElement.TryGetProperty("error", out var error) && error.TryGetProperty("type", out var type))
            {
                errorCode = type.GetString();
            }
        }
        catch (JsonException)
        {
            // Response bodies are deliberately excluded from the exception.
        }

        throw new AgentMemoryException(response.StatusCode, operation, errorCode);
    }

    private static void ValidateIdentifier(string value, string parameterName, int maximumLength = 255)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength)
        {
            throw new ArgumentException($"Identifier must contain 1-{maximumLength} characters.", parameterName);
        }
    }

    private static Uri EnsureTrailingSlash(Uri endpoint) => endpoint.AbsoluteUri.EndsWith("/", StringComparison.Ordinal)
        ? endpoint
        : new Uri(endpoint.AbsoluteUri + "/", UriKind.Absolute);

    private sealed record AddSessionEventResponse(SessionEvent Event);

    private sealed record GetSessionEventResponse(SessionEvent Event);
}
