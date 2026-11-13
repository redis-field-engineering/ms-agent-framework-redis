# Publishing setup

The `preview-release` workflow uses [NuGet trusted publishing](https://learn.microsoft.com/en-us/nuget/nuget-org/trusted-publishing) to obtain a short-lived API key through GitHub OIDC. Before enabling publication:

1. On NuGet.org, create a trusted publishing policy under the appropriate package owner with repository owner `redis-field-engineering`, repository `ms-agent-framework-redis`, workflow file `release.yml` (filename only), and environment `nuget`. Scope it to the six package IDs: `Redis.Iris.AgentFramework`, `Redis.Iris.AgentMemory`, `Redis.Iris.ContextRetriever`, `Redis.Iris.ContextRetriever.Modeling`, `Redis.Iris.ContextRetriever.Management`, and `Redis.Iris.AgentMemory.Management`, allowing new packages for the initial release and new versions for subsequent releases.
2. In the GitHub repository's `nuget` environment, set the `NUGET_USER` secret to the NuGet.org profile username of the user who created the policy, not an email address or organization name. Configure the environment's deployment branch restrictions and required reviewers for your release process.
3. Run `preview-release` from `main` with an existing preview tag reachable from `main`. Leave `publish` disabled to validate and package only; enable it to publish the validated artifacts.

Only the publish job can request an OIDC token. The release script receives the temporary key through `NUGET_API_KEY`; no long-lived `NUGET_API_KEY` GitHub secret is used. After the first successful trusted publishing release, revoke the old NuGet API key and remove its GitHub secret if present.
