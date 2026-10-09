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
