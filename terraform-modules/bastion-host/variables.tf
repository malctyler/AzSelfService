variable "name" {
  description = "Bastion host name."
  type        = string
}

variable "resource_group_name" {
  description = "Resource group containing the bastion host."
  type        = string
}

variable "location" {
  description = "Azure region."
  type        = string
}

variable "sku" {
  description = "Bastion SKU."
  type        = string
  default     = "Developer"
}

variable "copy_paste_enabled" {
  description = "Enable copy-paste support."
  type        = bool
  default     = true
}

variable "file_copy_enabled" {
  description = "Enable file copy support."
  type        = bool
  default     = false
}

variable "ip_connect_enabled" {
  description = "Enable IP connect."
  type        = bool
  default     = false
}

variable "kerberos_enabled" {
  description = "Enable Kerberos."
  type        = bool
  default     = false
}

variable "scale_units" {
  description = "Bastion scale units."
  type        = number
  default     = 2
}

variable "session_recording_enabled" {
  description = "Enable session recording."
  type        = bool
  default     = false
}

variable "shareable_link_enabled" {
  description = "Enable shareable links."
  type        = bool
  default     = false
}

variable "tunneling_enabled" {
  description = "Enable tunneling."
  type        = bool
  default     = false
}

variable "virtual_network_id" {
  description = "VNet resource ID used for Developer SKU bastion."
  type        = string
  default     = ""
}

variable "ip_configuration_name" {
  description = "IP configuration name for non-Developer SKUs."
  type        = string
  default     = "bastion-ip-config"
}

variable "subnet_id" {
  description = "Subnet resource ID for non-Developer SKUs (AzureBastionSubnet)."
  type        = string
  default     = ""
}

variable "public_ip_address_id" {
  description = "Public IP resource ID for non-Developer SKUs."
  type        = string
  default     = ""
}

variable "zones" {
  description = "Availability zones for Bastion host."
  type        = list(string)
  default     = []
}

variable "tags" {
  description = "Tags to apply to bastion host."
  type        = map(string)
  default     = {}
}
