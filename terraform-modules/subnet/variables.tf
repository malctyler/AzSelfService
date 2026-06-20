variable "name" {
  description = "Subnet name."
  type        = string
}

variable "resource_group_name" {
  description = "Resource group containing the virtual network."
  type        = string
}

variable "virtual_network_name" {
  description = "Virtual network name containing the subnet."
  type        = string
}

variable "address_prefixes" {
  description = "Subnet address prefixes."
  type        = list(string)
}

variable "service_endpoints" {
  description = "Service endpoints on the subnet."
  type        = list(string)
  default     = []
}

variable "default_outbound_access_enabled" {
  description = "Enable default outbound access."
  type        = bool
  default     = true
}

variable "private_endpoint_network_policies" {
  description = "Private endpoint network policies setting."
  type        = string
  default     = "Enabled"
}

variable "private_link_service_network_policies_enabled" {
  description = "Enable private link service network policies."
  type        = bool
  default     = true
}
