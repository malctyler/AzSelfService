output "vm_id" {
  description = "ID of the created Windows virtual machine."
  value       = azurerm_windows_virtual_machine.this.id
}

output "vm_name" {
  description = "Name of the created Windows virtual machine."
  value       = azurerm_windows_virtual_machine.this.name
}

output "public_ip_address" {
  description = "Public IP address assigned to the VM."
  value       = azurerm_public_ip.this.ip_address
}

output "private_ip_address" {
  description = "Private IP address assigned to the VM NIC."
  value       = azurerm_network_interface.this.private_ip_address
}

output "network_interface_id" {
  description = "Network interface ID attached to the VM."
  value       = azurerm_network_interface.this.id
}

output "vm_principal_id" {
  description = "Object (principal) ID of the VM's system-assigned managed identity. Assign Storage Blob Data Reader on the private blob container to enable key-free script downloads when post_install_use_managed_identity is true."
  value       = azurerm_windows_virtual_machine.this.identity[0].principal_id
}
