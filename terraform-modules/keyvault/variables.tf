variable "name" {
  description = "The name of the Key Vault."
  type        = string
}

variable "location" {
  description = "Azure region."
  type        = string
}

variable "resource_group_name" {
  description = "Resource group name."
  type        = string
}

variable "tenant_id" {
  description = "Azure AD tenant ID."
  type        = string
}

variable "sku_name" {
  description = "SKU name for Key Vault."
  type        = string
  default     = "standard"
}
