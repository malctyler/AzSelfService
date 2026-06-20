variable "name" {
  description = "Virtual network gateway name."
  type        = string
}

variable "resource_group_name" {
  description = "Resource group containing the virtual network gateway."
  type        = string
}

variable "location" {
  description = "Azure region."
  type        = string
}

variable "type" {
  description = "Gateway type."
  type        = string
  default     = "Vpn"
}

variable "sku" {
  description = "Gateway SKU."
  type        = string
  default     = "Basic"
}

variable "vpn_type" {
  description = "VPN type."
  type        = string
  default     = "RouteBased"
}

variable "generation" {
  description = "Gateway generation."
  type        = string
  default     = "Generation1"
}

variable "active_active" {
  description = "Enable active-active mode."
  type        = bool
  default     = false
}

variable "bgp_enabled" {
  description = "Enable BGP."
  type        = bool
  default     = false
}

variable "dns_forwarding_enabled" {
  description = "Enable DNS forwarding."
  type        = bool
  default     = false
}

variable "remote_vnet_traffic_enabled" {
  description = "Enable remote VNet traffic."
  type        = bool
  default     = false
}

variable "private_ip_address_enabled" {
  description = "Enable private IP address on gateway."
  type        = bool
  default     = false
}

variable "ip_sec_replay_protection_enabled" {
  description = "Enable IPSec replay protection."
  type        = bool
  default     = true
}

variable "virtual_wan_traffic_enabled" {
  description = "Enable virtual WAN traffic."
  type        = bool
  default     = false
}

variable "bgp_route_translation_for_nat_enabled" {
  description = "Enable BGP route translation for NAT."
  type        = bool
  default     = false
}

variable "ip_configuration" {
  description = "Gateway IP configuration."
  type = object({
    name                          = string
    public_ip_address_id          = string
    private_ip_address_allocation = optional(string, "Dynamic")
    subnet_id                     = string
  })
}

variable "bgp_settings" {
  description = "BGP settings used when bgp_enabled is true."
  type = object({
    asn                   = number
    peer_weight           = number
    ip_configuration_name = string
    apipa_addresses       = list(string)
  })
  default = {
    asn                   = 65515
    peer_weight           = 0
    ip_configuration_name = "default"
    apipa_addresses       = []
  }
}

variable "tags" {
  description = "Tags to apply to the virtual network gateway."
  type        = map(string)
  default     = {}
}
