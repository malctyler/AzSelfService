variable "name" {
  description = "VNet peering name."
  type        = string
}

variable "resource_group_name" {
  description = "Resource group containing the local VNet."
  type        = string
}

variable "virtual_network_name" {
  description = "Local virtual network name."
  type        = string
}

variable "remote_virtual_network_id" {
  description = "Remote virtual network resource ID."
  type        = string
}

variable "allow_virtual_network_access" {
  description = "Allow access between VNets."
  type        = bool
  default     = true
}

variable "allow_forwarded_traffic" {
  description = "Allow forwarded traffic."
  type        = bool
  default     = true
}

variable "allow_gateway_transit" {
  description = "Allow gateway transit."
  type        = bool
  default     = false
}

variable "use_remote_gateways" {
  description = "Use remote gateways."
  type        = bool
  default     = false
}
