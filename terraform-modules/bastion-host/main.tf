terraform {
  required_version = ">= 1.0"
  required_providers {
    azurerm = {
      source  = "hashicorp/azurerm"
      version = "~> 4.0"
    }
  }
  backend "azurerm" {}
}

provider "azurerm" {
  features {}
}

locals {
  developer_sku = lower(var.sku) == "developer"
}

resource "azurerm_bastion_host" "this" {
  name                = var.name
  location            = var.location
  resource_group_name = var.resource_group_name
  sku                 = var.sku

  copy_paste_enabled        = var.copy_paste_enabled
  file_copy_enabled         = var.file_copy_enabled
  ip_connect_enabled        = var.ip_connect_enabled
  kerberos_enabled          = var.kerberos_enabled
  scale_units               = var.scale_units
  session_recording_enabled = var.session_recording_enabled
  shareable_link_enabled    = var.shareable_link_enabled
  tunneling_enabled         = var.tunneling_enabled

  virtual_network_id = local.developer_sku ? var.virtual_network_id : null
  zones              = var.zones
  tags               = var.tags

  dynamic "ip_configuration" {
    for_each = local.developer_sku ? [] : [1]
    content {
      name                 = var.ip_configuration_name
      subnet_id            = var.subnet_id
      public_ip_address_id = var.public_ip_address_id
    }
  }
}
