variable "name" {
  description = "Network security rule name."
  type        = string
}

variable "resource_group_name" {
  description = "Resource group containing the NSG."
  type        = string
}

variable "network_security_group_name" {
  description = "NSG name containing this rule."
  type        = string
}

variable "priority" {
  description = "Rule priority."
  type        = number
}

variable "direction" {
  description = "Rule direction."
  type        = string
}

variable "access" {
  description = "Rule access."
  type        = string
}

variable "protocol" {
  description = "Rule protocol."
  type        = string
}

variable "description" {
  description = "Rule description."
  type        = string
  default     = ""
}

variable "source_port_range" {
  description = "Source port range."
  type        = string
  default     = "*"
}

variable "destination_port_range" {
  description = "Destination port range."
  type        = string
  default     = "*"
}

variable "source_address_prefix" {
  description = "Source address prefix."
  type        = string
  default     = "*"
}

variable "destination_address_prefix" {
  description = "Destination address prefix."
  type        = string
  default     = "*"
}

variable "source_port_ranges" {
  description = "Source port ranges."
  type        = list(string)
  default     = []
}

variable "destination_port_ranges" {
  description = "Destination port ranges."
  type        = list(string)
  default     = []
}

variable "source_address_prefixes" {
  description = "Source address prefixes."
  type        = list(string)
  default     = []
}

variable "destination_address_prefixes" {
  description = "Destination address prefixes."
  type        = list(string)
  default     = []
}

variable "source_application_security_group_ids" {
  description = "Source application security group IDs."
  type        = list(string)
  default     = []
}

variable "destination_application_security_group_ids" {
  description = "Destination application security group IDs."
  type        = list(string)
  default     = []
}
