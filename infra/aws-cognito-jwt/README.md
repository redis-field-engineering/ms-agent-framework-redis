# AWS Cognito delegated-JWT example

This Terraform example provisions the identity-provider side of Context Retriever delegated-user authorization. Cognito owns JWT signing and its public JWKS endpoint. A pre-token-generation Lambda copies trusted user-pool attributes into **access tokens** as `tenant_id` and `traveler_id`.

Surface IdP/JWKS configuration is currently a preview capability that is not exposed in the Redis Cloud UI or documented as a stable public admin API. By default Terraform only emits the required configuration. After the Context Retriever team confirms that it is enabled for the target service, set `configure_context_retriever_idp = true`, provide the surface ID, and authenticate `ctxctl` before applying. The optional `terraform_data` resource then configures the surface with the Cognito issuer.

## Deploy

```bash
cd infra/aws-cognito-jwt
cp terraform.tfvars.example terraform.tfvars
terraform init
terraform plan
terraform apply
```

No AWS resources are created until `terraform apply`. The optional `ctxctl` step relies on the CLI's existing admin credential store; no Redis admin key is placed in Terraform configuration or state.

Use a unique `name_prefix`; Cognito hosted-domain prefixes are globally unique within an AWS partition. Store Terraform state in an encrypted remote backend before using real users. `demo_users` temporary passwords are intentionally marked sensitive but still exist in state, so omit those resources in production and connect Cognito to the application's normal identity lifecycle.

## Authorization flow

Start an OAuth authorization-code + PKCE flow using the output `authorization_endpoint`. Request:

- `openid`
- `email`
- `https://redis-iris-context-retriever/context.read`
- `resource=https://redis-iris-context-retriever`

The `resource` parameter uses Cognito resource binding and puts the configured resource URI in the access token's `aud` claim. Forward the resulting **access token**, not the ID token, through `IContextRetrieverDelegatedTokenProvider`. The connector sends it as `Authorization: Bearer`, alongside the tenant agent key in `X-API-Key`.

The expected Context Retriever trust configuration is available from:

```bash
terraform output -json context_retriever_idp_configuration
```

The live test must prove that the delegated traveler token can only narrow the tenant agent key. Do not enable this path if the target deployment allows JWT-derived tags to replace or widen agent-key constraints.

## What Terraform creates

- Cognito user pool using the Essentials tier, required for V2 access-token customization
- Public authorization-code application client
- Hosted Cognito login domain
- Context Retriever resource server and `context.read` scope
- V2 pre-token-generation Lambda and least-privilege invocation permission
- Optional demo users with trusted tenant/traveler attributes

AWS documents that V2 pre-token-generation events can customize access-token claims and require the Essentials or Plus user-pool tier. Cognito publishes the signing public keys at the emitted `jwks_uri`; no private signing key leaves Cognito.
