# Redis Iris integration for Microsoft Agent Framework

Redis Field Engineering’s preview .NET connectors provide durable Agent Framework history, owner-scoped memory recall, and authorized Redis Context Retriever tools. The packages are maintained by Redis Field Engineering; they are not Microsoft products.

Use `Redis.Iris.AgentFramework` for the Microsoft Agent Framework integration. It brings in the `Redis.Iris.AgentMemory`, `Redis.Iris.ContextRetriever`, and `Redis.Iris.ContextRetriever.Modeling` packages; surface administration is available separately through `Redis.Iris.ContextRetriever.Management`.

Start with the [documentation site](https://redis-field-engineering.github.io/ms-agent-framework-redis/) or read the [standalone .NET quickstart source](docs/modules/ROOT/pages/quickstart.adoc).

To build or contribute to the Antora site, see the [documentation contributor guide](docs/README.md).

## Redis Agent Memory connector

`Redis.Iris.AgentMemory` is a standalone .NET client for the Redis Agent Memory (RAM) HTTP API. Use it when your application needs to store conversation events, retrieve sessions and summaries, search long-term memories, or delete session events and known memory records. It exposes `RedisAgentMemoryClient` through `IRedisAgentMemoryClient` and can be used independently of Microsoft Agent Framework.

Agent Memory provides two complementary kinds of memory:

| Memory | What it contains | When an application uses it |
| --- | --- | --- |
| Session memory | Conversation events and a service-generated summary, when available | Resume a conversation and provide its recent history to the model |
| Long-term memory | Searchable memories that can remain useful across conversations | Recall a user's preferences or other relevant facts in a new session |

For example, a traveler says “I prefer window seats” in one conversation. Session memory preserves that exchange. When the service's extraction process promotes the preference into long-term memory, the application can retrieve it during a later conversation for the same owner. Promotion is asynchronous and depends on the service configuration; saving an event does not mean the preference is immediately available for recall.

The connector handles HTTP requests, typed contracts, credentials, cancellation, and diagnostics. Redis Agent Memory performs storage, search, summarization, and background extraction. You need an existing Agent Memory endpoint, store ID, and agent credential; an ordinary Redis server alone does not expose this API. Deployments enforcing personal ownership also require a delegated user token.

When using the client directly, your application supplies search filters and authorizes access to sessions and records. The raw client does not automatically add an owner filter to every search. Deleting a session does not delete memories already extracted from it. See the [security model](https://github.com/redis-field-engineering/ms-agent-framework-redis/blob/main/SECURITY.md) and [API limits and persistence semantics](https://github.com/redis-field-engineering/ms-agent-framework-redis/blob/main/docs/operational-contract.md).

## Microsoft Agent Framework integration

`Redis.Iris.AgentFramework` connects the Agent Memory client to Agent Framework's history and context extension points. Your application supplies the model through an `IChatClient`; the providers supply remembered context and persist supported conversation messages.

| Provider | Role in an agent |
| --- | --- |
| `RedisIrisChatHistoryProvider` | Loads chronological session history and an optional summary before a run, then saves new supported messages afterward |
| `RedisIrisMemoryProvider` | Searches long-term memory using the latest external user message and a mandatory owner filter, then adds relevant results as explicitly untrusted context |
| `RedisContextRetrieverProvider` | Adds authorized MCP tools for retrieving current business records from a separate Context Retriever surface |

A typical memory-enabled turn works as follows:

1. The host resolves and authorizes the current user and session from trusted application state.
2. The history provider loads that session's conversation. The memory provider searches for relevant memories belonging to the same owner.
3. Agent Framework invokes the model with the current request, history, recalled context, and any configured tools.
4. The designated writer appends new supported request and response messages to Agent Memory. The service can extract long-term memories in the background.

Register the memory integration with `AddRedisIrisAgentMemory`, supplying an `IdentityResolver` and `AuthorizationPolicy`, then attach the providers to the agent as shown in the [quickstart](https://github.com/redis-field-engineering/ms-agent-framework-redis/blob/main/docs/quickstart.md#attach-agent-framework-providers). Registration coordinates a single event writer when both memory providers are used. Set `UseChatHistoryProvider = false` to use recall and event capture alongside another history implementation.

To resume a conversation after a restart, preserve the Agent Framework session state and its authorized RAM identity bindings. A new session for the same owner can recall long-term memories while maintaining its own conversation history. The application remains responsible for session storage and authentication; stored provider state never grants access by itself.

History windows, summary length, recall filters, similarity thresholds, and recalled-text budgets are configurable. History loads and writes fail closed by default; recall defaults to continuing without memories on an operation failure. The default mapper persists user and assistant text, so it does not reconstruct a full tool-call or multimodal transcript. See the [persistent sample](https://github.com/redis-field-engineering/ms-agent-framework-redis/blob/main/samples/RedisIrisPersistentAgent/README.md) for a complete application.

Context Retriever is optional for memory-only applications. It complements remembered preferences with current records, such as a traveler's flight reservation. See the [Context Retriever guide](https://github.com/redis-field-engineering/ms-agent-framework-redis/blob/main/docs/context-retriever.md) for per-user tool authorization and credential rotation.

## Documentation

- [Run the samples locally](https://github.com/redis-field-engineering/ms-agent-framework-redis/blob/main/LOCAL_DEVELOPMENT.md)
- [Security model](https://github.com/redis-field-engineering/ms-agent-framework-redis/blob/main/SECURITY.md)
- [Preview guarantees and limitations](https://github.com/redis-field-engineering/ms-agent-framework-redis/blob/main/docs/operational-contract.md)
- [Observability](https://github.com/redis-field-engineering/ms-agent-framework-redis/blob/main/OBSERVABILITY.md)
- [Context Retriever integration](https://github.com/redis-field-engineering/ms-agent-framework-redis/blob/main/docs/context-retriever.md)
- [Context Retriever POCO modeling](https://github.com/redis-field-engineering/ms-agent-framework-redis/blob/main/docs/context-retriever-modeling.md)
- [Context Retriever model sample](https://github.com/redis-field-engineering/ms-agent-framework-redis/blob/main/samples/RedisIrisContextModel/README.md)
- [Persistent .NET sample](https://github.com/redis-field-engineering/ms-agent-framework-redis/blob/main/samples/RedisIrisPersistentAgent/README.md)
- [ASP.NET Core DevUI sample](https://github.com/redis-field-engineering/ms-agent-framework-redis/blob/main/samples/RedisIrisDevUI/README.md)
- [C# Jupyter notebooks](https://github.com/redis-field-engineering/ms-agent-framework-redis/blob/main/notebooks/csharp/README.md)

## Supported versions

| Component | Supported baseline |
| --- | --- |
| Build SDK | .NET 10 (selected by `global.json`; required for `.slnx`) |
| Application runtimes | .NET 10 LTS recommended; .NET 8 compatibility tested until its November 10, 2026 end of support |
| Microsoft Agent Framework | 1.19.0 minimum; connector tested with 1.19.0 and 1.23.0 |
| Redis Agent Memory | Original `/v1/stores` API (default), or current self-managed Iris API with `ApiMode = AgentMemoryApiMode.Iris` |
| Redis Context Retriever | Managed MCP or self-managed 0.4.2 over loopback |

The connector, samples, and notebooks use Agent Framework 1.19.0 by default.

The libraries target `net8.0` and can be consumed by .NET 10 applications. The build SDK and library target framework are independent.

## License and package version

This repository is licensed under [MIT](https://github.com/redis-field-engineering/ms-agent-framework-redis/blob/main/LICENSE). All five packages use the same preview version, `0.1.0-preview.1`, declared in `src/Directory.Build.props`. Preview APIs may change before a stable release. A version in source does not mean the package has been published to NuGet.

### Direct long-term memory writes

Current-Iris deployments support `BulkCreateLongTermMemoriesAsync`,
`GetLongTermMemoryAsync`, `UpdateLongTermMemoryAsync`, and
`UpsertLongTermMemoryAsync`. Use stable IDs and request replay keys, inspect bulk
errors, and send the reviewed revision for edits. The Agent Framework writer adds
host authorization; opt-in agent tools prepare changes for explicit user approval.
The DevUI's `/memories` page creates and edits records and saves agent proposals.
See [the Agent Memory guide](docs/modules/ROOT/pages/agent-memory.adoc) and
[the sample configuration](samples/RedisIrisDevUI/README.md).
