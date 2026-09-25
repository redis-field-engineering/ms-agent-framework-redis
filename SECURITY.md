# Security model

The connector treats the application host—not prompts, tool arguments, or model output—as the authority for store, tenant, owner, session, actor, agent, and namespace selection. `IdentityResolver` receives only the trusted `AgentSession`; `AuthorizationPolicy` must independently approve its result. The managed store ID is fixed at registration and the standard recall API always adds an owner equality filter.

Do not derive identity or scope from message text. Doing so creates a confused-deputy path where an attacker can ask the agent to act with another user's authority. Cross-owner operations are intentionally separated under `RedisIrisAdministrativeMemorySearch.SearchAcrossOwnersAsync` and require an explicit authorization callback and reason.

Recalled memories and stored history are untrusted data. A compromised store or poisoned prior conversation can introduce indirect prompt injection. Recalled content is therefore injected as a user-role message labeled as untrusted context, never as a system instruction. Applications should still apply content controls appropriate to their threat model.

Use HTTPS, a least-privilege managed-service credential, and a rotating `IAgentMemoryCredentialProvider` in production. Never place credentials, raw message content, owner IDs, session IDs, or memory text in logs, span attributes, or metric labels.
