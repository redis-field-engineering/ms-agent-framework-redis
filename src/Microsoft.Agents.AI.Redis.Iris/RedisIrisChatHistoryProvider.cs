// Copyright (c) Redis, Inc.

using System.Net;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Redis.Iris.AgentMemory;

namespace Microsoft.Agents.AI.Redis.Iris;

/// <summary>Options for Redis Agent Memory-backed chat history.</summary>
public sealed class RedisIrisChatHistoryProviderOptions
{
    /// <summary>Gets or sets the maximum number of recent events returned to the model.</summary>
    public int? MaxMessagesToRetrieve { get; set; }

    /// <summary>Gets or sets the provider state-bag key.</summary>
    public string StateKey { get; set; } = nameof(RedisIrisChatHistoryProvider);

    /// <summary>Gets or sets serialization options for provider state.</summary>
    public JsonSerializerOptions? JsonSerializerOptions { get; set; }

    /// <summary>Gets or sets an optional filter applied to loaded history.</summary>
    public Func<IEnumerable<ChatMessage>, IEnumerable<ChatMessage>>? ProvideOutputMessageFilter { get; set; }

    /// <summary>Gets or sets an optional filter for externally supplied request messages.</summary>
    public Func<IEnumerable<ChatMessage>, IEnumerable<ChatMessage>>? StoreInputRequestMessageFilter { get; set; }

    /// <summary>Gets or sets an optional filter for response messages.</summary>
    public Func<IEnumerable<ChatMessage>, IEnumerable<ChatMessage>>? StoreInputResponseMessageFilter { get; set; }
}

/// <summary>Persists Agent Framework chat history in Redis Agent Memory session memory.</summary>
public sealed class RedisIrisChatHistoryProvider : ChatHistoryProvider
{
    private readonly IRedisAgentMemoryClient _client;
    private readonly RedisIrisMessageMapper _mapper;
    private readonly RedisIrisSessionEventWriter _eventWriter;
    private readonly ProviderSessionState<RedisIrisProviderState> _sessionState;
    private readonly int? _maxMessagesToRetrieve;
    private IReadOnlyList<string>? _stateKeys;

    /// <summary>Initializes a Redis Iris chat-history provider.</summary>
    public RedisIrisChatHistoryProvider(
        IRedisAgentMemoryClient client,
        Func<AgentSession?, RedisIrisProviderState> stateInitializer,
        RedisIrisMessageMapper? mapper = null,
        RedisIrisChatHistoryProviderOptions? options = null,
        RedisIrisSessionEventWriter? eventWriter = null)
        : base(
            options?.ProvideOutputMessageFilter,
            options?.StoreInputRequestMessageFilter ?? ExternalMessagesOnly,
            options?.StoreInputResponseMessageFilter)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(stateInitializer);
        options ??= new RedisIrisChatHistoryProviderOptions();
        if (options.MaxMessagesToRetrieve is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "MaxMessagesToRetrieve must be positive.");
        }

        this._client = client;
        this._mapper = mapper ?? new RedisIrisMessageMapper();
        this._eventWriter = eventWriter ?? new RedisIrisSessionEventWriter(client, RedisIrisEventWriter.ChatHistoryProvider, this._mapper);
        this._sessionState = new ProviderSessionState<RedisIrisProviderState>(stateInitializer, options.StateKey, options.JsonSerializerOptions);
        this._maxMessagesToRetrieve = options.MaxMessagesToRetrieve;
    }

    /// <inheritdoc />
    public override IReadOnlyList<string> StateKeys => this._stateKeys ??= [this._sessionState.StateKey];

    /// <inheritdoc />
    protected override async ValueTask<IEnumerable<ChatMessage>> ProvideChatHistoryAsync(InvokingContext context, CancellationToken cancellationToken = default)
    {
        var state = this._sessionState.GetOrInitializeState(context.Session);
        SessionMemory memory;
        try
        {
            memory = await this._client.GetSessionMemoryAsync(state.SessionId, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (AgentMemoryException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            return [];
        }

        if (!string.Equals(memory.OwnerId, state.OwnerId, StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException("The Redis Agent Memory session belongs to a different owner.");
        }

        IEnumerable<SessionEvent> events = memory.Events.OrderBy(static item => item.CreatedAt).ThenBy(static item => item.SystemTimestamp);
        if (this._maxMessagesToRetrieve is int maximum)
        {
            events = events.TakeLast(maximum);
        }

        return events.Select(this._mapper.ToChatMessage).ToList();
    }

    /// <inheritdoc />
    protected override async ValueTask StoreChatHistoryAsync(InvokedContext context, CancellationToken cancellationToken = default)
    {
        var state = this._sessionState.GetOrInitializeState(context.Session);
        await this._eventWriter.WriteAsync(
            RedisIrisEventWriter.ChatHistoryProvider,
            context.RequestMessages.Concat(context.ResponseMessages ?? []),
            state,
            cancellationToken).ConfigureAwait(false);
    }

    private static IEnumerable<ChatMessage> ExternalMessagesOnly(IEnumerable<ChatMessage> messages) =>
        messages.Where(static message => message.GetAgentRequestMessageSourceType() == AgentRequestMessageSourceType.External);
}
