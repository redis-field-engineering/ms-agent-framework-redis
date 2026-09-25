// Copyright (c) Redis, Inc.

using Microsoft.Agents.AI;
using Microsoft.Extensions.DependencyInjection;
using Redis.Iris.AgentMemory;

namespace Microsoft.Agents.AI.Redis.Iris;

/// <summary>Controls whether a provider operation fails the agent invocation.</summary>
public enum RedisIrisFailurePolicy
{
    /// <summary>Continue without the operation result.</summary>
    FailOpen,
    /// <summary>Fail the invocation.</summary>
    FailClosed,
}

/// <summary>Resolved application identities and scopes for one agent session.</summary>
public sealed record RedisIrisIdentityContext(
    string TenantId,
    string OwnerId,
    string SessionId,
    string ActorId,
    string AgentId,
    string? NamespaceId = null,
    IReadOnlyList<string>? Topics = null);

/// <summary>Configures the standard Redis Iris Agent Framework integration.</summary>
public sealed class RedisIrisAgentMemoryOptions
{
    /// <summary>Gets or sets the managed Agent Memory data-plane endpoint.</summary>
    public Uri? Endpoint { get; set; }
    /// <summary>Gets or sets the Agent Memory store ID.</summary>
    public string? StoreId { get; set; }
    /// <summary>Gets or sets a fixed bearer credential.</summary>
    public string? Credential { get; set; }
    /// <summary>Gets or sets a rotating credential provider instead of <see cref="Credential"/>.</summary>
    public IAgentMemoryCredentialProvider? CredentialProvider { get; set; }
    /// <summary>Gets or sets the required identity resolver, including tenant, owner, session, actor, agent, namespace, and topics.</summary>
    public Func<AgentSession?, RedisIrisIdentityContext>? IdentityResolver { get; set; }
    /// <summary>Gets or sets the required host authorization callback for resolved identities.</summary>
    public Func<RedisIrisIdentityContext, bool>? AuthorizationPolicy { get; set; }
    /// <summary>Gets or sets whether the chat-history provider is registered.</summary>
    public bool UseChatHistoryProvider { get; set; } = true;
    /// <summary>Gets or sets history-load failure behavior.</summary>
    public RedisIrisFailurePolicy HistoryLoadFailurePolicy { get; set; } = RedisIrisFailurePolicy.FailClosed;
    /// <summary>Gets or sets recall failure behavior.</summary>
    public RedisIrisFailurePolicy RecallFailurePolicy { get; set; } = RedisIrisFailurePolicy.FailOpen;
    /// <summary>Gets or sets event-write failure behavior.</summary>
    public RedisIrisFailurePolicy WriteFailurePolicy { get; set; } = RedisIrisFailurePolicy.FailClosed;
    /// <summary>Gets or sets chat-history options.</summary>
    public RedisIrisChatHistoryProviderOptions ChatHistory { get; set; } = new();
    /// <summary>Gets or sets memory-provider options.</summary>
    public RedisIrisMemoryProviderOptions Memory { get; set; } = new();
}

/// <summary>Dependency-injection registration for Redis Iris providers.</summary>
public static class RedisIrisServiceCollectionExtensions
{
    /// <summary>Registers the Agent Memory client, history provider, and long-term-memory provider.</summary>
    public static IServiceCollection AddRedisIrisAgentMemory(
        this IServiceCollection services,
        Action<RedisIrisAgentMemoryOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);
        var options = new RedisIrisAgentMemoryOptions();
        configure(options);
        Validate(options);

        var credentialProvider = options.CredentialProvider ?? new FixedAgentMemoryCredentialProvider(options.Credential!);
        var clientOptions = new RedisAgentMemoryClientOptions { Endpoint = options.Endpoint!, StoreId = options.StoreId! };
        services.AddSingleton(options);
        services.AddSingleton(credentialProvider);
        services.AddHttpClient("Redis.Iris.AgentMemory", client => client.BaseAddress = options.Endpoint);
        services.AddSingleton<IRedisAgentMemoryClient>(provider => new RedisAgentMemoryClient(
            provider.GetRequiredService<IHttpClientFactory>().CreateClient("Redis.Iris.AgentMemory"),
            clientOptions,
            credentialProvider));
        services.AddSingleton<RedisIrisMessageMapper>();
        services.AddSingleton<RedisIrisLongTermMemorySearch>();
        services.AddSingleton(provider => new RedisIrisSessionEventWriter(
            provider.GetRequiredService<IRedisAgentMemoryClient>(),
            options.UseChatHistoryProvider ? RedisIrisEventWriter.ChatHistoryProvider : RedisIrisEventWriter.MemoryProvider,
            provider.GetRequiredService<RedisIrisMessageMapper>()));

        Func<AgentSession?, RedisIrisProviderState> stateInitializer = session =>
        {
            var identity = options.IdentityResolver!(session) ?? throw new UnauthorizedAccessException("IdentityResolver could not resolve an identity.");
            ValidateIdentity(identity);
            if (!options.AuthorizationPolicy!(identity))
            {
                throw new UnauthorizedAccessException("The host application denied the resolved Redis Iris identity.");
            }

            return new(identity.SessionId, identity.OwnerId, identity.AgentId);
        };

        if (options.UseChatHistoryProvider)
        {
            services.AddSingleton(provider => new RedisIrisChatHistoryProvider(
                provider.GetRequiredService<IRedisAgentMemoryClient>(),
                stateInitializer,
                provider.GetRequiredService<RedisIrisMessageMapper>(),
                options.ChatHistory,
                provider.GetRequiredService<RedisIrisSessionEventWriter>()));
        }

        options.Memory.FailClosed = options.RecallFailurePolicy == RedisIrisFailurePolicy.FailClosed;
        options.Memory.CaptureSessionEvents = !options.UseChatHistoryProvider;
        services.AddSingleton(provider => new RedisIrisMemoryProvider(
            provider.GetRequiredService<RedisIrisLongTermMemorySearch>(),
            stateInitializer,
            options.Memory,
            provider.GetRequiredService<RedisIrisSessionEventWriter>(),
            provider.GetService<Microsoft.Extensions.Logging.ILoggerFactory>()));
        return services;
    }

    private static void Validate(RedisIrisAgentMemoryOptions options)
    {
        if (options.Endpoint is null || !options.Endpoint.IsAbsoluteUri || options.Endpoint.Scheme != Uri.UriSchemeHttps && options.Endpoint.Scheme != Uri.UriSchemeHttp)
        {
            throw new ArgumentException("Endpoint must be an absolute HTTP or HTTPS URI.", nameof(options.Endpoint));
        }

        if (string.IsNullOrWhiteSpace(options.StoreId))
        {
            throw new ArgumentException("StoreId is required.", nameof(options.StoreId));
        }

        if (options.CredentialProvider is null && string.IsNullOrWhiteSpace(options.Credential))
        {
            throw new ArgumentException("Credential or CredentialProvider is required.", nameof(options.Credential));
        }

        if (options.IdentityResolver is null)
        {
            throw new ArgumentException("IdentityResolver is required.", nameof(options.IdentityResolver));
        }

        if (options.AuthorizationPolicy is null)
        {
            throw new ArgumentException("AuthorizationPolicy is required.", nameof(options.AuthorizationPolicy));
        }

        if (options.Memory.Search.SimilarityThreshold is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options.Memory.Search.SimilarityThreshold), "SimilarityThreshold must be between 0 and 1.");
        }

        if (options.Memory.Search.Limit is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(options.Memory.Search.Limit), "Limit must be between 1 and 100.");
        }
    }

    private static void ValidateIdentity(RedisIrisIdentityContext identity)
    {
        if (string.IsNullOrWhiteSpace(identity.TenantId) ||
            string.IsNullOrWhiteSpace(identity.OwnerId) ||
            string.IsNullOrWhiteSpace(identity.SessionId) ||
            string.IsNullOrWhiteSpace(identity.ActorId) ||
            string.IsNullOrWhiteSpace(identity.AgentId))
        {
            throw new UnauthorizedAccessException("The trusted identity resolver returned an incomplete identity.");
        }
    }
}
