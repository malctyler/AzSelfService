variable "name" {
  description = "Name of the virtual network."
  type        = string
}

variable "resource_group_name" {
  description = "Resource group where the VNet and subnet are created."
  type        = string
}

variable "location" {
  description = "Azure region for all created resources."
  type        = string
}

variable "address_space" {
  description = "Address space for the virtual network in CIDR notation."
  type        = string
  default     = "10.0.0.0/16"

  validation {
    condition     = can(cidrhost(var.address_space, 0))
    error_message = "address_space must be a valid IPv4 CIDR block (for example 10.0.0.0/16)."
  }
}

variable "subnets" {
  description = "Optional explicit subnet definitions for import-safe management. When provided, subnet_count and subnet_X_service_endpoints are ignored."
  type = list(object({
    name                        = string
    address_prefix              = string
    service_endpoints           = optional(list(string), [])
    network_security_group_name = optional(string, "")
    network_security_group_id   = optional(string, "")
  }))
  default = []

  validation {
    condition = alltrue([
      for subnet in var.subnets :
      can(cidrhost(subnet.address_prefix, 0))
    ])
    error_message = "Each subnet address_prefix must be a valid IPv4 CIDR block."
  }

  validation {
    condition = alltrue([
      for subnet in var.subnets :
      alltrue([
        for endpoint in subnet.service_endpoints :
        contains(["Microsoft.Storage", "Microsoft.KeyVault"], endpoint)
      ])
    ])
    error_message = "Only Microsoft.Storage and Microsoft.KeyVault are supported service endpoints."
  }
}

variable "nsgs" {
  description = "Optional NSG definitions managed by this module. Intended for single-state VNet import/management when subnets reference network_security_group_name."
  type = list(object({
    name = string
    tags = optional(map(string), {})
    security_rules = optional(list(object({
      name                                       = string
      priority                                   = number
      direction                                  = string
      access                                     = string
      protocol                                   = string
      source_port_range                          = optional(string, "*")
      destination_port_range                     = optional(string, "*")
      source_address_prefix                      = optional(string, "*")
      destination_address_prefix                 = optional(string, "*")
      description                                = optional(string, null)
      source_port_ranges                         = optional(list(string), [])
      destination_port_ranges                    = optional(list(string), [])
      source_address_prefixes                    = optional(list(string), [])
      destination_address_prefixes               = optional(list(string), [])
      source_application_security_group_ids      = optional(list(string), [])
      destination_application_security_group_ids = optional(list(string), [])
    })), [])
  }))
  default = []
}

variable "subnet_count" {
  description = "Number of subnets to create (1-4)."
  type        = string
  default     = "1"

  validation {
    condition     = length(var.subnets) > 0 || contains(["1", "2", "3", "4"], var.subnet_count)
    error_message = "subnet_count must be one of: 1, 2, 3, or 4."
  }
}

variable "subnet_1_service_endpoints" {
  description = "Service endpoints to enable on subnet 1 (primary)."
  type        = list(string)
  default     = []

  validation {
    condition = alltrue([
      for endpoint in var.subnet_1_service_endpoints :
      contains(["Microsoft.Storage", "Microsoft.KeyVault"], endpoint)
    ])
    error_message = "Only Microsoft.Storage and Microsoft.KeyVault are supported service endpoints."
  }
}

variable "subnet_2_service_endpoints" {
  description = "Service endpoints to enable on subnet 2."
  type        = list(string)
  default     = []

  validation {
    condition = alltrue([
      for endpoint in var.subnet_2_service_endpoints :
      contains(["Microsoft.Storage", "Microsoft.KeyVault"], endpoint)
    ])
    error_message = "Only Microsoft.Storage and Microsoft.KeyVault are supported service endpoints."
  }
}

variable "subnet_3_service_endpoints" {
  description = "Service endpoints to enable on subnet 3."
  type        = list(string)
  default     = []

  validation {
    condition = alltrue([
      for endpoint in var.subnet_3_service_endpoints :
      contains(["Microsoft.Storage", "Microsoft.KeyVault"], endpoint)
    ])
    error_message = "Only Microsoft.Storage and Microsoft.KeyVault are supported service endpoints."
  }
}

variable "subnet_4_service_endpoints" {
  description = "Service endpoints to enable on subnet 4."
  type        = list(string)
  default     = []

  validation {
    condition = alltrue([
      for endpoint in var.subnet_4_service_endpoints :
      contains(["Microsoft.Storage", "Microsoft.KeyVault"], endpoint)
    ])
    error_message = "Only Microsoft.Storage and Microsoft.KeyVault are supported service endpoints."
  }
}

variable "enable_nsg" {
  description = "Legacy behavior: create and associate a single NSG with generated subnets. Ignored when subnets is provided."
  type        = string
  default     = "false"
}

variable "dns_servers" {
  description = "Comma-separated custom DNS server IPs. Leave empty for Azure default DNS."
  type        = string
  default     = ""
}

variable "tags" {
  description = "Additional tags to apply to all resources."
  type        = map(string)
  default     = {}
}
