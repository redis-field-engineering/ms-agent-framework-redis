using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Redis.Iris.ContextRetriever.Modeling;

namespace Redis.Iris.ContextRetriever.Management;

/// <summary>Direct HTTP access to the Context Retriever administrative surface API; no CLI dependency.</summary>
public sealed class ContextRetrieverManagementClient : IContextRetrieverManagementClient
{
    private const string SurfacePath = "api/v1/context-surfaces";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
    private readonly HttpClient _httpClient;
    private readonly Uri _baseUri;
    private readonly IContextRetrieverManagementCredentialProvider _credentials;

    /// <summary>Creates a client. The caller owns HttpClient and must disable redirects and cookies on its handler.</summary>
    /// <remarks>Each operation makes one send, with no automatic retries. Cancellation is not rollback.</remarks>
    public ContextRetrieverManagementClient(HttpClient httpClient, ContextRetrieverManagementClientOptions options,
        IContextRetrieverManagementCredentialProvider credentialProvider)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(credentialProvider);
        _baseUri = options.GetBaseUri();
        _httpClient = httpClient;
        _credentials = credentialProvider;
    }

    /// <inheritdoc />
    public Task<ContextSurface> CreateSurfaceAsync(ContextDataModel model, ContextSurfaceDeployment deployment, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(deployment);
        deployment.Validate();
        var body = new
        {
            name = deployment.Name,
            description = deployment.Description,
            metadata = deployment.Metadata,
            data_model = model,
            data_source = new
            {
                type = "redis",
                connection_config = new
                {
                    addr = deployment.RedisAddress,
                    username = deployment.RedisUsername ?? "",
                    password = deployment.RedisPassword ?? "",
                    db = deployment.RedisDatabase,
                    tls_enabled = deployment.RedisTls,
                },
            },
        };
        return SendAsync(HttpMethod.Post, SurfacePath, "surface.create", body, cancellationToken);
    }

    /// <inheritdoc />
    public Task<ContextSurface> GetSurfaceAsync(string surfaceId, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Get, GetSurfacePath(surfaceId), "surface.get", null, cancellationToken);

    /// <inheritdoc />
    public Task<ContextSurface> UpdateSurfaceAsync(string surfaceId, ContextSurfaceUpdate update, CancellationToken cancellationToken = default)
    {
        var path = GetSurfacePath(surfaceId);
        ArgumentNullException.ThrowIfNull(update);
        update.Validate();
        return SendAsync(HttpMethod.Put, path, "surface.update", update, cancellationToken);
    }

    private async Task<ContextSurface> SendAsync(HttpMethod method, string path, string operation, object? body, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var credential = await _credentials.GetCredentialAsync(cancellationToken).ConfigureAwait(false);
        FixedContextRetrieverManagementCredentialProvider.Validate(credential);
        using var request = new HttpRequestMessage(method, new Uri(_baseUri, path));
        request.Headers.Add("X-API-Key", credential);
        request.Headers.Accept.ParseAdd("application/json");
        if (body is not null) request.Content = JsonContent.Create(body, body.GetType(), options: JsonOptions);
        try
        {
            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw new ContextRetrieverManagementException(operation, response.StatusCode);
            var surface = await response.Content.ReadFromJsonAsync<ContextSurface>(JsonOptions, cancellationToken).ConfigureAwait(false);
            if (surface is null || string.IsNullOrWhiteSpace(surface.SurfaceId) || string.IsNullOrWhiteSpace(surface.Name))
                throw new ContextRetrieverManagementException(operation);
            return surface;
        }
        catch (ContextRetrieverManagementException) { throw; }
        catch (HttpRequestException) { throw new ContextRetrieverManagementException(operation); }
        catch (JsonException) { throw new ContextRetrieverManagementException(operation); }
    }

    private static string GetSurfacePath(string surfaceId)
    {
        if (string.IsNullOrWhiteSpace(surfaceId) || surfaceId is "." or ".." || surfaceId.Length > 255 ||
            surfaceId.Any(character => char.IsWhiteSpace(character) || char.IsControl(character) || character is '/' or '\\'))
            throw new ArgumentException("Surface ID must be a nonempty single path segment of at most 255 characters.", nameof(surfaceId));
        return $"{SurfacePath}/{Uri.EscapeDataString(surfaceId)}";
    }
}
