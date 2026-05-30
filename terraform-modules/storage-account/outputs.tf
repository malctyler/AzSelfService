output "id" {
  value       = azurerm_storage_account.this.id
  description = "The ID of the created storage account"
}

output "name" {
  value       = azurerm_storage_account.this.name
  description = "The name of the created storage account"
}

output "primary_blob_endpoint" {
  value       = azurerm_storage_account.this.primary_blob_endpoint
  description = "Primary blob endpoint URL"
}
