variable "name" {
  description = "Network security group name."
  type        = string
}

variable "resource_group_name" {
  description = "Resource group containing the NSG."
  type        = string
}

variable "location" {
  description = "Azure region for the NSG."
  type        = string
}

variable "security_rules" {
  description = "Optional inline NSG security rules."
  type = list(object({
    name                                       = string
    priority                                   = number
    direction                                  = string
    access                                     = string
    protocol                                   = string
    source_port_range                          = optional(string)
    destination_port_range                     = optional(string)
    source_address_prefix                      = optional(string)
    destination_address_prefix                 = optional(string)
    description                                = optional(string)
    source_port_ranges                         = optional(list(string))
    destination_port_ranges                    = optional(list(string))
    source_address_prefixes                    = optional(list(string))
    destination_address_prefixes               = optional(list(string))
    source_application_security_group_ids      = optional(list(string))
    destination_application_security_group_ids = optional(list(string))
  }))
  default = []
}

variable "tags" {
  description = "Tags to apply to the NSG."
  type        = map(string)
  default     = {}
}
