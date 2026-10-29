# Publishing a public preview

The manual `preview-release` workflow validates an existing version tag. It does not run on merge or create tags. `publish` defaults to false. No NuGet publication has been performed as part of preparing this repository.

1. Complete the [release quality gate](../TESTING.md), including fresh two-user signed RAM isolation. Local signed-user RAM and delegated Context Retriever validation passed on October 29, 2026; the workflow must rerun the gates for the selected tag. The repository is currently internal and its documentation is not anonymously accessible. Package documentation links point here, so the publishing job requires public repository visibility. Changing visibility is a separate owner-controlled release decision; this workflow never changes it. If documentation will be hosted elsewhere, update and validate every package URL and the publication check first.
2. Set the next unused preview version in `src/Directory.Build.props`, review the changes and release notes, and merge them. All four packages share this version. Never move a published tag or reuse a package version for different content.
3. Create and push the corresponding tag on the reviewed main commit, such as `v0.1.0-preview.1`.
4. Run `preview-release` with that tag and `publish=false`. The workflow requires the tag to be reachable from main and its version to match the package version. It builds/tests both runtimes, checks the complete package set and a fresh consumer, and requires all three live gates to pass without skips.
5. After the validation run succeeds, run with the same immutable tag and `publish=true` to repeat validation and publish those validated artifacts. The `nuget` environment supplies the publishing key. Configure its protection and publisher ownership in GitHub/NuGet before first use.

## Required GitHub environment configuration

Create the `release-validation` environment with these secrets. Use dedicated synthetic fixtures reachable by the GitHub runner; local port forwards are not reachable from a hosted runner.

| Secrets | Purpose |
| --- | --- |
| `RAM_LIVE_ENDPOINT`, `RAM_LIVE_STORE_ID`, `RAM_LIVE_API_KEY` | Key-only development store with extraction enabled and a threshold below 24 session events |
| `RAM_DELEGATED_ENDPOINT`, `RAM_DELEGATED_STORE_ID`, `RAM_DELEGATED_API_KEY` | Separate ownership-enforcing deployment |
| `RAM_DELEGATED_USERS_JSON` | Two fresh signed users: `subject`, `accessToken`, `expiresAt`, as documented in TESTING.md |
| `CONTEXT_LIVE_ENDPOINT`, `CONTEXT_USERS_JSON` | Existing synthetic two-traveler surface and scoped credential bundle |

Refresh short-lived tokens before each run with enough remaining lifetime for build and promotion validation. Use the configured authorization-code flow; never manufacture tokens or disable ownership checks to obtain a passing release. Temporary token bundles are mode 0600 and removed in an `always()` step. Do not upload test logs or bundles that might contain customer data or credentials.

Create the `nuget` environment with `NUGET_API_KEY`, scoped to these four package IDs. The publish job consumes the exact artifacts produced by the successful validation job and publishes dependencies before the framework adapter. A partial NuGet upload fails the job; inspect which versions already exist before recovery. The workflow deliberately does not suppress duplicate-version errors or overwrite published packages.

## Release record

The four assemblies embed portable PDBs and their source documents. Consumers can
debug the exact packaged source without a separate symbol upload or repository
access. SourceLink mappings still identify the originating commit. `eng/pack.sh`
inspects the actual package assemblies and rejects missing embedded symbols,
source documents, or SourceLink mappings.

Record the tag/commit, four package versions, runtime and service versions, identity modes, live gate results, and known preview limits in the release notes. Managed scoped-key Context Retriever validation is not evidence for delegated Context Retriever authentication. Changes to Agent Framework or service compatibility require rerunning the same gates. The MIT license applies to this library; Redis service and third-party dependency terms remain separate.
