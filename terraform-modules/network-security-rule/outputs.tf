output "id" {
  description = "ID of the network security rule."
  value       = azurerm_network_security_rule.this.id
}

output "name" {
  description = "Name of the network security rule."
  value       = azurerm_network_security_rule.this.name
}
