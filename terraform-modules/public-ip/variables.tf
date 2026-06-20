variable "name" {
  description = "Public IP name."
  type        = string
}

variable "resource_group_name" {
  description = "Resource group containing the Public IP."
  type        = string
}

variable "location" {
  description = "Azure region."
  type        = string
}

variable "allocation_method" {
  description = "Dynamic or Static allocation."
  type        = string
  default     = "Dynamic"
}

variable "sku" {
  description = "Public IP SKU."
  type        = string
  default     = "Basic"
}

variable "sku_tier" {
  description = "Public IP SKU tier."
  type        = string
  default     = "Regional"
}

variable "ip_version" {
  description = "IP version."
  type        = string
  default     = "IPv4"
}

variable "idle_timeout_in_minutes" {
  description = "Idle timeout in minutes."
  type        = number
  default     = 4
}

variable "ddos_protection_mode" {
  description = "DDoS protection mode."
  type        = string
  default     = "VirtualNetworkInherited"
}

variable "zones" {
  description = "Availability zones for the Public IP."
  type        = list(string)
  default     = []
}

variable "tags" {
  description = "Tags to apply to the Public IP."
  type        = map(string)
  default     = {}
}
