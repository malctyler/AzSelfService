# Resource Group Module

This is a foundational Terraform module for Azure Resource Groups.

## Usage

```hcl
module "resource_group" {
  source = "./terraform-modules/resource-group"
  
  name       = "my-resource-group"
  location   = "eastus"
  environment = "dev"
  tags = {
    CostCenter = "Engineering"
    Owner      = "Platform Team"
  }
}
```

## Inputs

| Name | Description | Type | Required | Default |
|------|-------------|------|----------|---------|
| name | Name of the resource group | string | Yes | N/A |
| location | Azure region | string | Yes | N/A |
| environment | Environment (dev/staging/prod) | string | No | "dev" |
| tags | Additional tags | object | No | {} |

## Outputs

| Name | Description |
|------|-------------|
| id | Resource group ID |
| name | Resource group name |
| location | Resource group location |

## Constraints

- Resource group name must be 1-90 characters
- Name can only contain alphanumeric, hyphens, and underscores
- Location must be a valid Azure region
