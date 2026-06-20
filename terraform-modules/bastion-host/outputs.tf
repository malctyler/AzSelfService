output "id" {
  description = "ID of the bastion host."
  value       = azurerm_bastion_host.this.id
}

output "name" {
  description = "Name of the bastion host."
  value       = azurerm_bastion_host.this.name
}
