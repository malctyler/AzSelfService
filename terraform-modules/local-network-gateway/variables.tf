variable "name" {
  description = "Local network gateway name."
  type        = string
}

variable "resource_group_name" {
  description = "Resource group containing the local network gateway."
  type        = string
}

variable "location" {
  description = "Azure region."
  type        = string
}

variable "gateway_address" {
  description = "Public IP address of the on-premises VPN device."
  type        = string
  default     = ""
}

variable "gateway_fqdn" {
  description = "FQDN of the on-premises VPN device."
  type        = string
  default     = ""
}

variable "address_space" {
  description = "Address spaces behind the local gateway."
  type        = list(string)
}

variable "tags" {
  description = "Tags to apply to the local network gateway."
  type        = map(string)
  default     = {}
}
