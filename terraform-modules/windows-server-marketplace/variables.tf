variable "name" {
  description = "Name of the Windows VM."
  type        = string
}

variable "resource_group_name" {
  description = "Resource group where the VM and networking resources are created."
  type        = string
}

variable "location" {
  description = "Azure region for all created resources."
  type        = string
}

variable "sku" {
  description = "Simplified VM size selector."
  type        = string
  default     = "b2s"

  validation {
    condition     = contains(["b2s", "d2s_v5", "d4s_v5"], var.sku)
    error_message = "sku must be one of: b2s, d2s_v5, d4s_v5."
  }
}

variable "admin_username" {
  description = "Local administrator username for the Windows VM."
  type        = string
}

variable "admin_password" {
  description = "Local administrator password for the Windows VM."
  type        = string
  sensitive   = true
}

variable "subnet_id" {
  description = "ARM resource ID of an existing subnet to attach the VM NIC to."
  type        = string
}

variable "rdp_allowed_cidr" {
  description = "CIDR allowed to RDP (3389). For production, restrict this to trusted IP ranges."
  type        = string
  default     = "0.0.0.0/0"
}

variable "tags" {
  description = "Additional tags to apply to all resources."
  type        = map(string)
  default     = {}
}

# ---------------------------------------------------------------------------
# Optional domain join
# ---------------------------------------------------------------------------

variable "domain_name" {
  description = "FQDN of the Active Directory domain to join (e.g. corp.example.com). Leave blank to skip domain join."
  type        = string
  default     = ""
}

variable "domain_join_username" {
  description = "Account with permission to join computers to the domain (UPN or DOMAIN\\user format). Required when domain_name is set."
  type        = string
  default     = ""
}

variable "domain_join_password" {
  description = "Password for the domain join account. Required when domain_name is set."
  type        = string
  default     = ""
  sensitive   = true
}

variable "domain_join_ou_path" {
  description = "Distinguished Name of the OU for the computer account (e.g. OU=Servers,DC=corp,DC=example,DC=com). Leave blank to use the default Computers container."
  type        = string
  default     = ""
}

variable "dns_servers" {
  description = "Custom DNS server IP addresses to set on the NIC. Required for domain join — must include the IP of the Active Directory domain controller so the VM can locate a DC."
  type        = list(string)
  default     = []
}

# ---------------------------------------------------------------------------
# Optional post-build software installation via Chocolatey
# ---------------------------------------------------------------------------

variable "chocolatey_packages" {
  description = "List of Chocolatey package IDs to install after provisioning (e.g. [\"adobereader\", \"7zip\", \"googlechrome\"]). Browse packages at https://community.chocolatey.org/packages."
  type        = list(string)
  default     = []
}

variable "post_install_script_uri" {
  description = "HTTPS URI of a PowerShell .ps1 script to download and execute. For private Azure Blob Storage, use a User Delegation SAS URL (no key required) or enable post_install_use_managed_identity and assign Storage Blob Data Reader to the vm_principal_id output."
  type        = string
  default     = ""
}

variable "post_install_use_managed_identity" {
  description = "When true, the VM's system-assigned managed identity fetches the post_install_script_uri blob via an IMDS bearer token instead of CSE's native download. No storage account key is required. Assign Storage Blob Data Reader on the container to the vm_principal_id output after first apply."
  # string instead of bool so the portal can pass \"\" (unset/hidden field) without a type error.
  # Accepted values: \"true\", \"false\", or \"\" (treated as false).
  type    = string
  default = "false"

  validation {
    condition     = contains(["true", "false", ""], var.post_install_use_managed_identity)
    error_message = "post_install_use_managed_identity must be \"true\" or \"false\"."
  }
}
