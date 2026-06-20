output "id" {
  description = "ID of the local network gateway."
  value       = azurerm_local_network_gateway.this.id
}

output "name" {
  description = "Name of the local network gateway."
  value       = azurerm_local_network_gateway.this.name
}
