output "id" {
  description = "ID of the network security group."
  value       = azurerm_network_security_group.this.id
}

output "name" {
  description = "Name of the network security group."
  value       = azurerm_network_security_group.this.name
}
