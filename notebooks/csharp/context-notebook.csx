// Shared configuration for notebooks 03–05. Never display this configuration or credential bundles.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Redis.Iris.ContextRetriever;

public static class ContextNotebook
{
    public static string FindRoot()
    {
        for (var directory = new DirectoryInfo(Environment.CurrentDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "ms-agent-framework-redis.slnx"))) return directory.FullName;
        throw new InvalidOperationException("Start the kernel inside this repository.");
    }

    public static string Root { get; } = FindRoot();
    private static readonly Dictionary<string, string> Settings = LoadSettings();

    private static Dictionary<string, string> LoadSettings()
    {
        var path = Environment.GetEnvironmentVariable("REDIS_IRIS_ENV_FILE") ?? Path.Combine(Root, ".env.local");
        if (!File.Exists(path)) return new(StringComparer.Ordinal);
        if (Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase))
            return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path))!;
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in File.ReadLines(path))
        {
            var entry = line.Trim();
            if (entry.StartsWith("export ", StringComparison.Ordinal)) entry = entry[7..].TrimStart();
            if (entry.Length == 0 || entry.StartsWith('#')) continue;
            var separator = entry.IndexOf('=');
            if (separator < 1) continue;
            var value = entry[(separator + 1)..].Trim();
            if (value.Length >= 2 && (value[0] == '"' && value[^1] == '"' || value[0] == '\'' && value[^1] == '\''))
                value = value[1..^1];
            result[entry[..separator].Trim()] = value;
        }
        return result;
    }

    public static string? Optional(string name) => Environment.GetEnvironmentVariable(name) ?? Settings.GetValueOrDefault(name);
    public static string Required(string name) => Optional(name) is { Length: > 0 } value
        ? value : throw new InvalidOperationException($"Set {name} in the environment or selected configuration file.");

    public static string ArtifactPath(string file) => Path.Combine(Root, "artifacts", "context-retriever", file);

    public static NotebookTraveler LoadTraveler(string travelerId = "traveler-001")
    {
        var path = Optional("REDIS_IRIS_CONTEXT_RETRIEVER_USERS_FILE") ?? ArtifactPath("redis-iris-travel-users.json");
        path = Path.IsPathRooted(path) ? path : Path.Combine(Root, path);
        if (!File.Exists(path)) throw new InvalidOperationException("Run notebook 00 or set REDIS_IRIS_CONTEXT_RETRIEVER_USERS_FILE to the scoped credential bundle.");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        var users = root.ValueKind == JsonValueKind.Array ? root : root.GetProperty("users");
        var user = users.EnumerateArray().Single(item => item.GetProperty("travelerId").GetString() == travelerId);
        string RequiredProperty(string name) => (user.TryGetProperty(name, out var value) ? value : root.GetProperty(name)).GetString()!;
        IContextRetrieverDelegatedTokenProvider? tokenProvider = null;
        if (user.TryGetProperty("contextRetrieverDelegatedToken", out var token) && token.ValueKind == JsonValueKind.String)
            tokenProvider = new FixedContextRetrieverDelegatedTokenProvider(new(
                token.GetString()!, user.GetProperty("contextRetrieverDelegatedTokenVersion").GetString()!,
                user.GetProperty("contextRetrieverDelegatedTokenExpiresAt").GetDateTimeOffset()));
        return new NotebookTraveler(
            user.GetProperty("tenantId").GetString()!, travelerId,
            new Uri(Required("REDIS_IRIS_CONTEXT_RETRIEVER_MCP_ENDPOINT")),
            new FixedContextRetrieverCredentialProvider(new(
                RequiredProperty("contextRetrieverAgentKey"), RequiredProperty("contextRetrieverCredentialVersion"))),
            tokenProvider);
    }
}

public sealed class NotebookTraveler
{
    public string TenantId { get; }
    public string TravelerId { get; }
    private readonly Uri endpoint;
    private readonly IContextRetrieverCredentialProvider credentials;
    private readonly IContextRetrieverDelegatedTokenProvider? tokens;

    public NotebookTraveler(string tenantId, string travelerId, Uri endpoint,
        IContextRetrieverCredentialProvider credentials, IContextRetrieverDelegatedTokenProvider? tokens)
    {
        TenantId = tenantId; TravelerId = travelerId; this.endpoint = endpoint;
        this.credentials = credentials; this.tokens = tokens;
    }

    // Selection is trusted notebook/application code, never an argument supplied by the model.
    public ContextRetrieverAccessProfile Profile() => new(endpoint, $"notebook:{TenantId}:{TravelerId}", credentials,
        new Dictionary<string, IReadOnlyDictionary<string, object?>>
        {
            ["get_traveler_by_id"] = new Dictionary<string, object?> { ["id"] = TravelerId },
        }, tokens);

    public async System.Threading.Tasks.Task<IContextRetrieverClient> ConnectAsync()
    {
        var credential = await credentials.GetCredentialAsync();
        var token = tokens is null ? null : await tokens.GetTokenAsync();
        return await new ContextRetrieverClientFactory().ConnectAsync(new() { Endpoint = endpoint }, credential.AgentKey, token?.Token);
    }

    public override string ToString() => $"Notebook traveler: {TenantId}/{TravelerId}";
}
