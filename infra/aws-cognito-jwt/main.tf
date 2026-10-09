data "aws_caller_identity" "current" {}

data "archive_file" "claims_trigger" {
  type        = "zip"
  source_file = "${path.module}/lambda/index.mjs"
  output_path = "${path.module}/.terraform/claims-trigger.zip"
}

resource "aws_iam_role" "claims_trigger" {
  name_prefix = "${var.name_prefix}-claims-"
  assume_role_policy = jsonencode({
    Version = "2012-10-17"
    Statement = [{
      Effect    = "Allow"
      Principal = { Service = "lambda.amazonaws.com" }
      Action    = "sts:AssumeRole"
    }]
  })
}

resource "aws_iam_role_policy_attachment" "claims_trigger_logs" {
  role       = aws_iam_role.claims_trigger.name
  policy_arn = "arn:aws:iam::aws:policy/service-role/AWSLambdaBasicExecutionRole"
}

resource "aws_lambda_function" "claims_trigger" {
  function_name    = "${var.name_prefix}-claims"
  role             = aws_iam_role.claims_trigger.arn
  runtime          = "nodejs22.x"
  handler          = "index.handler"
  filename         = data.archive_file.claims_trigger.output_path
  source_code_hash = data.archive_file.claims_trigger.output_base64sha256
}

resource "aws_lambda_permission" "allow_cognito" {
  statement_id   = "AllowCognitoUserPool"
  action         = "lambda:InvokeFunction"
  function_name  = aws_lambda_function.claims_trigger.function_name
  principal      = "cognito-idp.amazonaws.com"
  source_account = data.aws_caller_identity.current.account_id
}

resource "aws_cognito_user_pool" "this" {
  name                = "${var.name_prefix}-users"
  user_pool_tier      = "ESSENTIALS"
  username_attributes = ["email"]

  admin_create_user_config {
    allow_admin_create_user_only = true
  }

  password_policy {
    minimum_length                   = 12
    require_lowercase                = true
    require_numbers                  = true
    require_symbols                  = true
    require_uppercase                = true
    temporary_password_validity_days = 7
  }

  schema {
    name                     = "tenant_id"
    attribute_data_type      = "String"
    developer_only_attribute = false
    mutable                  = true
    required                 = false
    string_attribute_constraints {
      min_length = 1
      max_length = 128
    }
  }

  schema {
    name                     = "traveler_id"
    attribute_data_type      = "String"
    developer_only_attribute = false
    mutable                  = true
    required                 = false
    string_attribute_constraints {
      min_length = 1
      max_length = 128
    }
  }

  lambda_config {
    pre_token_generation_config {
      lambda_arn     = aws_lambda_function.claims_trigger.arn
      lambda_version = "V2_0"
    }
  }

  depends_on = [aws_lambda_permission.allow_cognito]
}

resource "aws_cognito_resource_server" "context_retriever" {
  identifier   = var.context_retriever_audience
  name         = "Redis Iris Context Retriever"
  user_pool_id = aws_cognito_user_pool.this.id

  scope {
    scope_name        = "context.read"
    scope_description = "Call governed Context Retriever tools"
  }
}

resource "aws_cognito_user_pool_client" "application" {
  name         = "${var.name_prefix}-web"
  user_pool_id = aws_cognito_user_pool.this.id

  generate_secret                      = false
  allowed_oauth_flows_user_pool_client = true
  allowed_oauth_flows                  = ["code"]
  supported_identity_providers         = ["COGNITO"]
  callback_urls                        = var.callback_urls
  logout_urls                          = var.logout_urls
  allowed_oauth_scopes = [
    "openid",
    "email",
    "${aws_cognito_resource_server.context_retriever.identifier}/context.read",
  ]
  explicit_auth_flows = ["ALLOW_REFRESH_TOKEN_AUTH", "ALLOW_USER_SRP_AUTH"]

  access_token_validity  = 15
  id_token_validity      = 15
  refresh_token_validity = 1

  token_validity_units {
    access_token  = "minutes"
    id_token      = "minutes"
    refresh_token = "days"
  }
}

resource "aws_cognito_user_pool_domain" "this" {
  domain       = var.name_prefix
  user_pool_id = aws_cognito_user_pool.this.id
}

resource "aws_cognito_user" "demo" {
  for_each = toset(keys(nonsensitive(var.demo_users)))

  user_pool_id       = aws_cognito_user_pool.this.id
  username           = var.demo_users[each.key].email
  temporary_password = var.demo_users[each.key].temporary_password
  message_action     = "SUPPRESS"

  attributes = {
    email                = var.demo_users[each.key].email
    email_verified       = "true"
    "custom:tenant_id"   = var.demo_users[each.key].tenant_id
    "custom:traveler_id" = var.demo_users[each.key].traveler_id
  }
}

resource "terraform_data" "context_retriever_idp" {
  count = var.configure_context_retriever_idp ? 1 : 0

  triggers_replace = [
    var.context_retriever_surface_id,
    aws_cognito_user_pool.this.id,
    aws_cognito_user_pool_client.application.id,
    var.context_retriever_audience,
  ]

  provisioner "local-exec" {
    command = <<-EOT
      ctxctl surface idp configure '${var.context_retriever_surface_id}' \
        --issuer 'https://cognito-idp.${var.aws_region}.amazonaws.com/${aws_cognito_user_pool.this.id}' \
        --jwks-uri 'https://cognito-idp.${var.aws_region}.amazonaws.com/${aws_cognito_user_pool.this.id}/.well-known/jwks.json' \
        --audience '${var.context_retriever_audience}' \
        --user-claim 'sub' \
        --access-tag-claims '{"tenant_id":"tenant_id","traveler_id":"traveler_id"}' \
        --require-user-token
    EOT
  }
}
