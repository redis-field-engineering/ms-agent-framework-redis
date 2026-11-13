// Copyright (c) Redis, Inc.

using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Redis.Iris.AgentMemory.Management;

/// <summary>Custom-type administration using the dedicated Iris control-plane v2 memory-types resources.</summary>
/// <remarks>No automatic retries or store-wide read/modify/write. The caller owns HttpClient and must disable redirects and cookies.</remarks>
public sealed class AgentMemoryManagementClient : IAgentMemoryManagementClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter<CustomMemoryFieldType>(allowIntegerValues: false) },
    };
    private readonly HttpClient _http;
    private readonly Uri _baseUri;
    private readonly IAgentMemoryManagementCredentialProvider _credentials;

    /// <summary>Creates an administrative client with independently configured endpoint and credentials.</summary>
    public AgentMemoryManagementClient(HttpClient httpClient, AgentMemoryManagementClientOptions options,
        IAgentMemoryManagementCredentialProvider credentialProvider)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(credentialProvider);
        _baseUri = options.GetBaseUri();
        _http = httpClient;
        _credentials = credentialProvider;
    }

    /// <inheritdoc />
    public async Task<MemoryTypeListResult> ListMemoryTypesAsync(string storeId, int limit = 50, string? cursor = null, CancellationToken cancellationToken = default)
    {
        var path = StoreTypesPath(storeId);
        if (limit is < 1 or > 200) throw new ArgumentOutOfRangeException(nameof(limit));
        if (cursor is not null && cursor.Length is < 1 or > 1024) throw new ArgumentException("Cursor must contain 1-1024 characters.", nameof(cursor));
        path += $"?limit={limit}";
        if (cursor is not null) path += "&cursor=" + Uri.EscapeDataString(cursor);
        var result = await SendAsync<MemoryTypeListResult>(HttpMethod.Get, path, "memory_type.list", null, cancellationToken).ConfigureAwait(false);
        if (result.Items is null) throw new AgentMemoryManagementException("memory_type.list");
        foreach (var item in result.Items) CheckResponse(item, "memory_type.list");
        return result;
    }

    /// <inheritdoc />
    public async Task<CustomMemoryTypeDefinition> GetMemoryTypeAsync(string storeId, string name, CancellationToken cancellationToken = default)
    {
        var result = await SendAsync<CustomMemoryTypeDefinition>(HttpMethod.Get, TypePath(storeId, name), "memory_type.get", null, cancellationToken).ConfigureAwait(false);
        CheckResponse(result, "memory_type.get", name);
        return result;
    }

    /// <inheritdoc />
    public async Task<CustomMemoryTypeDefinition> CreateMemoryTypeAsync(string storeId, CustomMemoryTypeDefinition definition, CancellationToken cancellationToken = default)
    {
        var path = StoreTypesPath(storeId);
        ArgumentNullException.ThrowIfNull(definition);
        ValidateName(definition.Name);
        if (new[] { "semantic", "episodic", "message", "session_summary", "session_summary_view" }.Contains(definition.Name, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException("Custom type names cannot collide with built-in memory types.", nameof(definition));
        ValidateDescription(definition.Description);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in definition.Fields ?? [])
        {
            ArgumentNullException.ThrowIfNull(field);
            ValidateName(field.Name);
            ValidateDescription(field.Description);
            if (!Enum.IsDefined(field.Type)) throw new ArgumentException("Unsupported custom field type.", nameof(definition));
            if (!names.Add(field.Name)) throw new ArgumentException("Custom field names must be unique.", nameof(definition));
        }
        if (definition.ExtractionStrategy is { } strategy) ValidatePrompt(strategy.Prompt);
        var result = await SendAsync<CustomMemoryTypeDefinition>(HttpMethod.Post, path, "memory_type.create", new
        {
            definition.Name,
            definition.Description,
            definition.Fields,
            definition.ExtractionStrategy,
        }, cancellationToken).ConfigureAwait(false);
        CheckResponse(result, "memory_type.create", definition.Name);
        return result;
    }

    /// <inheritdoc />
    public async Task<CustomMemoryTypeDefinition> UpdateExtractionStrategyAsync(string storeId, string name, MemoryExtractionStrategyUpdate update,
        string? expectedRevision = null, CancellationToken cancellationToken = default)
    {
        var path = TypePath(storeId, name);
        ArgumentNullException.ThrowIfNull(update);
        if (update.Prompt is null && update.Enabled is null) throw new ArgumentException("Specify a prompt, enabled flag, or both.", nameof(update));
        if (update.Prompt is not null) ValidatePrompt(update.Prompt);
        if (expectedRevision is not null && expectedRevision.Length is < 1 or > 64)
            throw new ArgumentException("ExpectedRevision must contain 1-64 characters.", nameof(expectedRevision));
        var result = await SendAsync<CustomMemoryTypeDefinition>(HttpMethod.Patch, path, "memory_type.strategy.update",
            new { ExtractionStrategy = update, ExpectedRevision = expectedRevision }, cancellationToken).ConfigureAwait(false);
        CheckResponse(result, "memory_type.strategy.update", name);
        return result;
    }

    private async Task<T> SendAsync<T>(HttpMethod method, string path, string operation, object? body, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Snapshot the request before yielding to a credential provider or transport.
        var payload = body is null ? null : JsonSerializer.SerializeToUtf8Bytes(body, JsonOptions);
        var credential = await _credentials.GetCredentialAsync(cancellationToken).ConfigureAwait(false);
        FixedAgentMemoryManagementCredentialProvider.Validate(credential);
        using var request = new HttpRequestMessage(method, new Uri(_baseUri, path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        request.Headers.Accept.ParseAdd("application/json");
        if (payload is not null)
        {
            request.Content = new ByteArrayContent(payload);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }
        try
        {
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw new AgentMemoryManagementException(operation, response.StatusCode);
            return await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken).ConfigureAwait(false)
                ?? throw new AgentMemoryManagementException(operation);
        }
        catch (AgentMemoryManagementException) { throw; }
        catch (HttpRequestException) { throw new AgentMemoryManagementException(operation); }
        catch (JsonException) { throw new AgentMemoryManagementException(operation); }
    }

    private static void CheckResponse(CustomMemoryTypeDefinition? definition, string operation, string? expectedName = null)
    {
        if (definition is null || string.IsNullOrWhiteSpace(definition.Name) || string.IsNullOrWhiteSpace(definition.Description) ||
            expectedName is not null && definition.Name != expectedName)
            throw new AgentMemoryManagementException(operation);
    }

    private static string StoreTypesPath(string storeId)
    {
        if (storeId is null || storeId.Length != 35 || !storeId.StartsWith("st_", StringComparison.Ordinal) ||
            storeId.AsSpan(3).ContainsAnyExcept("0123456789abcdef"))
            throw new ArgumentException("Store ID must be st_ followed by 32 lowercase hexadecimal characters.", nameof(storeId));
        return $"iris/v1/agent-memory/stores/{storeId}/memory-types";
    }

    private static string TypePath(string storeId, string name)
    {
        ValidateName(name);
        return StoreTypesPath(storeId) + "/" + Uri.EscapeDataString(name);
    }

    private static void ValidateName(string name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 64 || !char.IsAsciiLetter(name[0]) ||
            name.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '_' and not '-'))
            throw new ArgumentException("Names must be 1-64 characters: a letter followed by letters, digits, underscores or hyphens.", nameof(name));
    }

    private static void ValidateDescription(string description)
    {
        if (string.IsNullOrWhiteSpace(description) || description.Length > 200)
            throw new ArgumentException("Descriptions must contain 1-200 characters.", nameof(description));
    }

    private static void ValidatePrompt(string prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt) || prompt.Length > 10_000)
            throw new ArgumentException("Extraction prompts must contain 1-10000 characters.", nameof(prompt));
    }
}
