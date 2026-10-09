locals {
  issuer   = "https://cognito-idp.${var.aws_region}.amazonaws.com/${aws_cognito_user_pool.this.id}"
  jwks_uri = "${local.issuer}/.well-known/jwks.json"
}

output "user_pool_id" {
  value = aws_cognito_user_pool.this.id
}

output "application_client_id" {
  value = aws_cognito_user_pool_client.application.id
}

output "issuer" {
  value = local.issuer
}

output "jwks_uri" {
  value = local.jwks_uri
}

output "context_retriever_audience" {
  value = var.context_retriever_audience
}

output "authorization_endpoint" {
  value = "https://${aws_cognito_user_pool_domain.this.domain}.auth.${var.aws_region}.amazoncognito.com/oauth2/authorize"
}

output "token_endpoint" {
  value = "https://${aws_cognito_user_pool_domain.this.domain}.auth.${var.aws_region}.amazoncognito.com/oauth2/token"
}

output "context_retriever_idp_configuration" {
  value = {
    issuer             = local.issuer
    jwks_uri           = local.jwks_uri
    audience           = var.context_retriever_audience
    user_claim         = "sub"
    access_tag_claims  = { tenant_id = "tenant_id", traveler_id = "traveler_id" }
    require_user_token = true
  }
}

output "devui_oidc_environment" {
  description = "Non-secret settings that opt the DevUI sample into this Cognito user pool."
  value = {
    DEVUI_AUTH_MODE                     = "oidc"
    DEVUI_OIDC_AUTHORITY                = local.issuer
    DEVUI_OIDC_CLIENT_ID                = aws_cognito_user_pool_client.application.id
    DEVUI_OIDC_AUDIENCE                 = var.context_retriever_audience
    DEVUI_OIDC_CONTEXT_SCOPE            = "${var.context_retriever_audience}/context.read"
    DEVUI_OIDC_LOGOUT_ENDPOINT          = "https://${aws_cognito_user_pool_domain.this.domain}.auth.${var.aws_region}.amazoncognito.com/logout"
    DEVUI_OIDC_POST_LOGOUT_REDIRECT_URI = var.logout_urls[0]
  }
}

output "demo_user_credentials" {
  description = "Demo-only Cognito usernames and temporary passwords. Read with terraform output -json demo_user_credentials."
  sensitive   = true
  value = {
    for key, user in var.demo_users : key => {
      username           = user.email
      temporary_password = coalesce(user.temporary_password, random_password.demo[key].result)
      tenant_id          = user.tenant_id
      traveler_id        = user.traveler_id
    }
  }
}
