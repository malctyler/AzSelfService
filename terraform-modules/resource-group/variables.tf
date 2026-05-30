variable "name" {
  type        = string
  description = "Name of the resource group"

  validation {
    condition     = can(regex("^[a-zA-Z0-9-_]*$", var.name))
    error_message = "Name must contain only alphanumeric characters, hyphens, and underscores."
  }
}

variable "location" {
  type        = string
  description = "Azure region for the resource group"

  validation {
    condition     = contains(["eastus", "westus", "eastus2", "westeurope", "southeastasia", "northeurope", "uksouth", "ukwest"], var.location)
    error_message = "Location must be a valid Azure region."
  }
}

variable "environment" {
  type        = string
  description = "Environment (dev, staging, prod)"
  default     = "dev"
}

variable "tags" {
  type        = map(string)
  description = "Additional tags to apply to the resource group"
  default     = {}
}
