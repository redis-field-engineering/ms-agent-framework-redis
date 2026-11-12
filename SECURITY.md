# Security model

The connector treats the application host—not prompts, tool arguments, or model output—as the authority for store, tenant, owner, session, actor, agent, and namespace selection. `IdentityResolver` receives only the trusted `AgentSession`; `AuthorizationPolicy` must independently approve its result. The managed store ID is fixed at registration and the standard recall API always adds an owner equality filter.

Do not derive identity or scope from message text. Doing so creates a confused-deputy path where an attacker can ask the agent to act with another user's authority. Cross-owner operations are intentionally separated under `RedisIrisAdministrativeMemorySearch.SearchAcrossOwnersAsync` and require an explicit authorization callback and reason.

Recalled memories and stored history are untrusted data. A compromised store or poisoned prior conversation can introduce indirect prompt injection. Recalled content is therefore injected as a user-role message labeled as untrusted context, never as a system instruction. Applications should still apply content controls appropriate to their threat model.

Use HTTPS, a least-privilege managed-service credential, and a rotating `IAgentMemoryCredentialProvider` in production. Never place credentials, raw message content, owner IDs, session IDs, or memory text in logs, span attributes, or metric labels.

## Delegated RAM user authentication

Configure `RedisIrisAgentMemoryOptions.DelegatedTokenProvider` with an
`IAgentMemoryDelegatedTokenProvider` to send a user access JWT alongside the
agent key. RAM receives `Authorization: Bearer <agent key>` and
`X-Iris-User-Token: <user JWT>`. The low-level client also accepts the provider
through its four-argument constructor; the original constructor remains available.

The provider runs on every HTTP request and must read trusted authentication
state. A configured provider returning a missing, empty, or expired token fails
before HTTP dispatch. Authorization errors are never retried with the JWT
omitted. Credentials are attached to individual requests, not shared default
headers. The DI HTTP client disables default HTTP request logging because paths
contain session identifiers. The credential-header redaction configuration remains
available to loggers that honor it; custom loggers receive raw requests and must
sanitize paths and credentials themselves. Connector metrics and activities remain available.
Hosts adding HTTP logging must sanitize paths and redact `X-Iris-User-Token`
as well as `Authorization`.

The server must support delegated tokens, trust the issuer and audience, and
enable ownership enforcement. Forwarding a JWT alone does not enable isolation.
The agent key retains store permissions; the JWT supplies the user ID. The host's
`ownerId` and initial USER event actor must match that ID. Existing application
owner filters, session isolation, and authorization policies remain necessary.

The DevUI opts in with `REDIS_IRIS_RAM_DELEGATED_AUTH=true` and requires OIDC.
It reuses the validated server-side access-token ticket, uses Cognito `sub` as
the RAM owner, and includes that subject in its session isolation key. Token
expiry requires sign-in again. Old composite-owner data is not migrated or
retrieved through a less restricted fallback.

## Session authorization

`AddRedisIrisAgentMemory` resolves and authorizes the current caller before each history load, recall, and event write, including resumed sessions. Persisted state locates memory and must match the newly authorized identity; it does not grant authority. Hosts constructing providers directly should supply `stateValidator` and perform the equivalent per-operation check. Keep session storage protected against unauthorized modification.

## Agent Memory transport

Production endpoints require HTTPS; plain HTTP is accepted only on loopback for development. The DI registration disables automatic redirects and cookies. Applications supplying their own `HttpClient` must also set `AllowAutoRedirect = false` and `UseCookies = false` on its handler: .NET can forward custom headers, including `X-Iris-User-Token`, across redirects. The connector cannot inspect an arbitrary caller-owned handler pipeline.

## Tenant, namespace, and topic scope

Resolved namespace IDs are carried to session writes and recall filters; history reads verify the returned namespace. Resolved topics constrain recall with an all-topics filter, intersected with configured filters. RAM session events have no topic field, so this does not assign topics to extracted memories. Tenant, namespace, and topic identity are rechecked against persisted session state on every operation. Previously serialized state without these bindings must be recreated by the trusted host after authorizing the session.

RAM has no tenant filter in these operations. `TenantId` binds host authorization to a session; it does not create a server-side partition. In a shared store, use globally unique owner IDs and/or distinct authorized namespace resources for tenants. Never rely on identical owner IDs plus different `TenantId` strings alone. For delegated RAM, owner IDs must remain the validated JWT subject; use namespaces or separate stores for additional tenant boundaries.

## Direct long-term memory writes

The raw client exposes current-Iris create, get, PATCH, and PUT operations. It does
not authorize individual records on the application's behalf. Use
`RedisIrisLongTermMemoryWriter` for ordinary user operations: it binds owner,
namespace, and mandatory topics to the trusted current identity, revalidates
session bindings and edit targets, and sends the revision that was reviewed.
The writer's exact-namespace scope includes only unnamespaced records when no
namespace is configured. Ownership transfers and cross-namespace edits require
separately authorized administrative code using the raw client.

`MemoryWrites.AuthorizeWriteAsync` denies by default. Approve only a specific
immutable proposal associated with an explicit authenticated user action. Agent
memory tools can prepare proposals but cannot approve or execute them. The sample
keeps proposals in a bounded, expiring in-memory store and uses antiforgery-protected
Save requests. The agent's text and tool arguments cannot set approval, identity,
credentials, or expected revisions. Proposal text and recalled memory are untrusted
data; the editor renders them as text. Direct writes do not apply the service's
extraction exclusions; hosts can add `ValidateContentAsync` to enforce their policy.

Conditional writes require a server-provided revision. HTTP 412 is a conflict,
never permission to overwrite unconditionally. A successful write followed by a
failed refresh is reported as committed with refresh pending. Retry the original
proposal; do not generate new IDs or replay keys after an uncertain response.
