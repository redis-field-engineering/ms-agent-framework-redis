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
headers. The DI HTTP client redacts both headers. Hosts configuring their own
HTTP logging must also redact `X-Iris-User-Token`.

The server must support delegated tokens, trust the issuer and audience, and
enable ownership enforcement. Forwarding a JWT alone does not enable isolation.
The agent key retains store permissions; the JWT supplies the user ID. The host's
`ownerId` and initial USER event actor must match that ID. Existing application
owner filters, session isolation, and authorization policies remain necessary.

The DevUI opts in with `REDIS_IRIS_RAM_DELEGATED_AUTH=true` and requires OIDC.
It reuses the validated server-side access-token ticket, uses Cognito `sub` as
the RAM owner, and includes that subject in its session isolation key. Token
expiry requires sign-in again. Old composite-owner data is not migrated or
retrieved through a less restricted fallback. The demo's static RAM tenant
mapping to account `1` applies only to its single-account deployment.

## Agent Memory transport

Production endpoints require HTTPS; plain HTTP is accepted only on loopback for development. The DI registration disables automatic redirects and cookies. Applications supplying their own `HttpClient` must also set `AllowAutoRedirect = false` and `UseCookies = false` on its handler: .NET can forward custom headers, including `X-Iris-User-Token`, across redirects. The connector cannot inspect an arbitrary caller-owned handler pipeline.
