terraform {
  required_version = ">= 1.0"
  required_providers {
    azurerm = {
      source  = "hashicorp/azurerm"
      version = "~> 3.0"
    }
  }
}

provider "azurerm" {
  features {}
}

resource "azurerm_resource_group" "rg" {
  name     = var.name
  location = var.location

  tags = merge(
    var.tags,
    {
      "Environment"   = var.environment
      "ManagedBy"     = "AzSelfService"
      "CreatedAt"     = timestamp()
    }
  )
}
