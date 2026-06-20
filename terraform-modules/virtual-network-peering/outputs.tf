output "id" {
  description = "ID of the virtual network peering."
  value       = azurerm_virtual_network_peering.this.id
}

output "name" {
  description = "Name of the virtual network peering."
  value       = azurerm_virtual_network_peering.this.name
}
