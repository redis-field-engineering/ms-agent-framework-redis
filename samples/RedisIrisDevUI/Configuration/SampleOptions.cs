// Copyright (c) Redis, Inc.

using Redis.Iris.ContextRetriever;
using Redis.Iris.AgentMemory;

namespace RedisIrisDevUI;

// Read and validate environment configuration once, before registering services.
internal sealed class SampleOptions
{
    public string Endpoint { get; }
    public AgentMemoryApiMode ApiMode { get; }
    public string StoreId { get; }
    public string IrisApiKey { get; }
    public string OpenAiApiKey { get; }
    public string ContextRetrieverEndpoint { get; }
    public ContextRetrieverCredential? ContextRetrieverCredential { get; }
    public string OwnerId { get; }
    public string Model { get; }
    public int ShortTermWindow { get; }
    public bool RamDelegatedAuth { get; }
    public bool AllowRemoteAccess { get; }
    public bool ConsoleTelemetry { get; }
    public bool AccessTokenInspectorEnabled { get; }
    public TravelAuthenticationMode AuthenticationMode { get; }
    public TravelUserDirectory UserDirectory { get; }
    public OidcSettings? Oidc { get; }
    public bool AuthenticationRequired => Oidc is not null || UserDirectory.IsMultiUser;

    public SampleOptions(IConfiguration configuration)
    {
        Endpoint = Required(configuration, "REDIS_IRIS_ENDPOINT");
        var apiMode = configuration["REDIS_IRIS_API_MODE"] ?? "Legacy";
        ApiMode = Enum.TryParse<AgentMemoryApiMode>(apiMode, ignoreCase: true, out var parsedMode) && Enum.IsDefined(parsedMode)
            ? parsedMode
            : throw new InvalidOperationException("REDIS_IRIS_API_MODE must be Legacy or Iris.");
        StoreId = Required(configuration, "REDIS_IRIS_STORE_ID");
        IrisApiKey = Required(configuration, "REDIS_IRIS_API_KEY");
        OpenAiApiKey = Required(configuration, "OPENAI_API_KEY");
        ContextRetrieverEndpoint = Required(configuration, "REDIS_IRIS_CONTEXT_RETRIEVER_MCP_ENDPOINT");
        OwnerId = configuration["SAMPLE_OWNER_ID"] ?? "redis-iris-devui-shopper";
        Model = configuration["OPENAI_MODEL"] ?? "gpt-4.1-mini";
        ShortTermWindow = ParsePositiveInt(configuration, "REDIS_IRIS_SHORT_TERM_WINDOW", 4);
        AuthenticationMode = TravelAuthentication.ParseMode(configuration["DEVUI_AUTH_MODE"]);
        RamDelegatedAuth = ParseBoolean(configuration, "REDIS_IRIS_RAM_DELEGATED_AUTH", false);
        if (RamDelegatedAuth && AuthenticationMode != TravelAuthenticationMode.OpenIdConnect)
        {
            throw new InvalidOperationException("REDIS_IRIS_RAM_DELEGATED_AUTH requires DEVUI_AUTH_MODE=oidc.");
        }

        Oidc = AuthenticationMode == TravelAuthenticationMode.OpenIdConnect ? new OidcSettings(configuration) : null;
        UserDirectory = Oidc is not null
            ? TravelUserDirectory.Parse(null)
            : TravelUserDirectory.Load(configuration["DEVUI_USERS_FILE"], configuration["DEVUI_USERS_JSON"]);
        AccessTokenInspectorEnabled = Oidc is not null && ParseBoolean(configuration, "DEVUI_ENABLE_ACCESS_TOKEN_INSPECTOR", false);
        AllowRemoteAccess = ParseBoolean(configuration, "DEVUI_ALLOW_REMOTE_ACCESS", false);
        ConsoleTelemetry = ParseBoolean(configuration, "REDIS_IRIS_DEMO_TELEMETRY", false);

        var credentialVersion = configuration["REDIS_IRIS_CONTEXT_RETRIEVER_CREDENTIAL_VERSION"] ?? "environment-v1";
        ContextRetrieverCredential = Oidc is not null
            ? new ContextRetrieverCredential(Required(configuration, "REDIS_IRIS_CONTEXT_RETRIEVER_AGENT_KEY"), credentialVersion)
            : UserDirectory.AgentCredential ?? (UserDirectory.IsMultiUser
                ? null
                : new ContextRetrieverCredential(Required(configuration, "REDIS_IRIS_CONTEXT_RETRIEVER_AGENT_KEY"), credentialVersion));
    }

    public TravelUser CreateLegacyUser() => new(
        Subject: OwnerId,
        DisplayName: OwnerId,
        TenantId: "redis-iris-devui",
        TravelerId: OwnerId,
        SignInToken: "legacy-single-user-mode",
        ContextRetrieverAgentKey: null,
        ContextRetrieverCredentialVersion: null,
        ContextRetrieverDelegatedToken: null,
        ContextRetrieverDelegatedTokenVersion: null,
        ContextRetrieverDelegatedTokenExpiresAt: null);

    internal static string Required(IConfiguration configuration, string name) => configuration[name]
        ?? throw new InvalidOperationException($"Set the {name} environment variable.");

    private static int ParsePositiveInt(IConfiguration configuration, string name, int fallback)
    {
        var value = configuration[name];
        return value is null
            ? fallback
            : int.TryParse(value, out var parsed) && parsed > 0
                ? parsed
                : throw new InvalidOperationException($"{name} must be a positive integer.");
    }

    private static bool ParseBoolean(IConfiguration configuration, string name, bool fallback)
    {
        var value = configuration[name];
        return value is null
            ? fallback
            : bool.TryParse(value, out var parsed)
                ? parsed
                : throw new InvalidOperationException($"{name} must be true or false.");
    }
}

internal sealed class OidcSettings
{
    public string Authority { get; }
    public string ClientId { get; }
    public string Audience { get; }
    public string LogoutEndpoint { get; }
    public string PostLogoutRedirectUri { get; }
    public string ContextScope { get; }

    public OidcSettings(IConfiguration configuration)
    {
        Authority = SampleOptions.Required(configuration, "DEVUI_OIDC_AUTHORITY").TrimEnd('/');
        ClientId = SampleOptions.Required(configuration, "DEVUI_OIDC_CLIENT_ID");
        Audience = SampleOptions.Required(configuration, "DEVUI_OIDC_AUDIENCE");
        LogoutEndpoint = SampleOptions.Required(configuration, "DEVUI_OIDC_LOGOUT_ENDPOINT");
        PostLogoutRedirectUri = SampleOptions.Required(configuration, "DEVUI_OIDC_POST_LOGOUT_REDIRECT_URI");
        ContextScope = configuration["DEVUI_OIDC_CONTEXT_SCOPE"] ?? $"{Audience.TrimEnd('/')}/context.read";

        if (!Uri.TryCreate(Authority, UriKind.Absolute, out var authorityUri) || authorityUri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException("DEVUI_OIDC_AUTHORITY must be an absolute HTTPS URI.");
        }
        if (!Uri.TryCreate(LogoutEndpoint, UriKind.Absolute, out var logoutUri) || logoutUri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException("DEVUI_OIDC_LOGOUT_ENDPOINT must be an absolute HTTPS URI.");
        }
        if (!Uri.TryCreate(PostLogoutRedirectUri, UriKind.Absolute, out _))
        {
            throw new InvalidOperationException("DEVUI_OIDC_POST_LOGOUT_REDIRECT_URI must be an absolute URI registered with Cognito.");
        }
    }
}
