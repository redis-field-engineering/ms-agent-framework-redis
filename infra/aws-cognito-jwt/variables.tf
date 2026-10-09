variable "aws_region" {
  description = "AWS region for the Cognito user pool and Lambda trigger."
  type        = string
  default     = "us-east-1"
}

variable "name_prefix" {
  description = "Prefix for provisioned AWS resources and the Cognito hosted-login domain."
  type        = string
  default     = "redis-iris-maf"
}

variable "callback_urls" {
  description = "Exact OIDC callback URLs allowed for the deployed application."
  type        = list(string)
  default     = ["http://localhost:5080/signin-oidc"]
}

variable "logout_urls" {
  description = "Exact post-logout URLs allowed for the deployed application."
  type        = list(string)
  default     = ["http://localhost:5080/"]

  validation {
    condition     = length(var.logout_urls) > 0
    error_message = "At least one logout URL is required for the DevUI OIDC configuration."
  }
}

variable "context_retriever_audience" {
  description = "HTTPS resource indicator requested during authorization and validated by Context Retriever."
  type        = string
  default     = "https://redis-iris-context-retriever"

  validation {
    condition     = can(regex("^https://", var.context_retriever_audience))
    error_message = "context_retriever_audience must be an HTTPS URI."
  }
}

variable "demo_users" {
  description = "Optional demo users. Temporary passwords are sensitive and remain in Terraform state; omit users for production."
  sensitive   = true
  type = map(object({
    email              = string
    temporary_password = string
    tenant_id          = string
    traveler_id        = string
  }))
  default = {}
}

variable "configure_context_retriever_idp" {
  description = "Run the preview ctxctl surface IdP command after provisioning Cognito. Leave false unless Redis confirms support for the target service."
  type        = bool
  default     = false
}

variable "context_retriever_surface_id" {
  description = "Context Retriever surface to configure when configure_context_retriever_idp is true."
  type        = string
  default     = ""

  validation {
    condition     = !var.configure_context_retriever_idp || length(trimspace(var.context_retriever_surface_id)) > 0
    error_message = "context_retriever_surface_id is required when configure_context_retriever_idp is true."
  }
}
