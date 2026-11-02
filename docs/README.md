# Documentation site

The public documentation uses [Antora](https://docs.antora.org/antora/latest/),
native AsciiDoc, the default Antora UI with a Redis overlay, and local Lunr search.

## Build and preview

Use Node.js 22 or newer. Run from the repository root:

```bash
npm ci
npm run docs:check
npm run docs:serve
```

Open <http://localhost:8080>. `npm run docs:preview` combines the build and server;
rebuild after content changes. The server binds only to loopback. No .NET build,
Redis service, model credential, or `.env` file is needed to build documentation.
The first build downloads the default UI bundle and caches it under `.cache/antora`.
Dependencies and search tooling are pinned by `package-lock.json`; the upstream
default UI uses its maintained `bundle-stable` artifact. For fully offline builds,
install dependencies and populate the UI cache first.

## Author content

- Edit the canonical guides in `modules/ROOT/pages/*.adoc`.
- Add page navigation in `modules/ROOT/nav.adoc`.
- Use `xref:page.adoc[Label]` for links between site pages.
- Keep repository paths in code examples relative to the repository root.
- Configure the component and shared attributes in `antora.yml`.
- Configure the site, URL, output, extensions, and UI in `../antora-playbook.yml`.
- Customize templates and styles in `supplemental-ui`.

The component is unversioned while the packages are in preview. Package versions
do not create documentation versions automatically. For a future versioned site,
assign versions in the component descriptor and explicitly select release branches
or tags in the playbook.

The playbook reads the current checkout (`branches: HEAD`), including uncommitted
edits and detached pull-request checkouts. It never fetches documentation from
`main` behind your back. Generated output, Node dependencies, and Antora caches
are ignored by Git.

The old Markdown guide paths remain as pointers, including their existing section
anchors. Keep detailed edits in the AsciiDoc sources. `SECURITY.md` remains the
authoritative security model; the site's security page summarizes and links it.
Sample READMEs and the notebook README remain the detailed operational instructions
beside their runnable source, linked from the site's sample catalog.

`npm run docs:check` fails on Antora warnings, unresolved cross-references, missing
local HTML links, section fragments, assets, or required publishing artifacts.
It does not execute sample commands or check external services.

## Publish with GitHub Pages

The workflow in `../.github/workflows/docs.yml` builds and checks documentation
on relevant pull requests and pushes. Pull requests only upload an artifact;
pushes to `main` and manual runs on `main` also deploy it.

For the first publication, a repository administrator must select **Settings →
Pages → Build and deployment → Source → GitHub Actions**. Merge the changes into
`main` or run the Documentation workflow on `main`. No PAT or additional secret
is required; the deploy job uses the built-in `GITHUB_TOKEN` and OIDC permissions.
Repository or organization rules may require approval for the `github-pages`
environment.

The configured production URL is
<https://redis-field-engineering.github.io/ms-agent-framework-redis/>.
It becomes live after the first successful deployment. To use a custom domain,
update `site.url` in the playbook, the Markdown compatibility pointers, and the
repository's Pages domain settings.
