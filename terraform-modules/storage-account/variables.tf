variable "name" {
  type        = string
  description = "Storage account name"

  validation {
    condition     = can(regex("^[a-z0-9]{3,24}$", var.name))
    error_message = "Storage account name must be 3-24 lowercase letters and numbers."
  }
}

variable "resource_group_name" {
  type        = string
  description = "Resource group where the storage account will be created"
}

variable "location" {
  type        = string
  description = "Azure region for the storage account"

  validation {
    condition     = contains(["eastus", "westus", "eastus2", "westeurope", "southeastasia", "northeurope", "uksouth", "ukwest"], var.location)
    error_message = "Location must be a valid Azure region."
  }
}

variable "account_tier" {
  type        = string
  description = "Storage account performance tier"
  default     = "Standard"

  validation {
    condition     = contains(["Standard", "Premium"], var.account_tier)
    error_message = "account_tier must be Standard or Premium."
  }
}

variable "account_replication_type" {
  type        = string
  description = "Storage replication option"
  default     = "LRS"

  validation {
    condition     = contains(["LRS", "GRS", "RAGRS", "ZRS", "GZRS", "RAGZRS"], var.account_replication_type)
    error_message = "account_replication_type must be one of LRS, GRS, RAGRS, ZRS, GZRS, RAGZRS."
  }
}

variable "tags" {
  type        = map(string)
  description = "Additional tags to apply to the storage account"
  default     = {}
}
