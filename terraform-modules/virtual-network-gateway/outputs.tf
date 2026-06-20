output "id" {
  description = "ID of the virtual network gateway."
  value       = azurerm_virtual_network_gateway.this.id
}

output "name" {
  description = "Name of the virtual network gateway."
  value       = azurerm_virtual_network_gateway.this.name
}
