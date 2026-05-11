# AzSelfService Platform — Module Framework

## Overview

This document defines how Terraform modules become self-describing, reusable products in the platform. It covers module structure, metadata, versioning, validation, and extensibility.

---

## Core Principle: Modules as Products

### Module = Infrastructure as a Product

Each Terraform module in AzSelfService is treated as a **product** with:

- **Code:** Terraform resource definitions (main.tf, variables.tf, outputs.tf)
- **Metadata:** Name, version, description (module.yaml)
- **Schema:** Input validation rules and types (schema.json)
- **UI Definition:** Form layout and components (ui_schema in module.yaml)
- **Documentation:** README with examples
- **Lifecycle:** Versioning, deprecation, archival

**Why:** Modules are not one-off scripts; they are reusable infrastructure components that scale as the platform grows. Treating them as products enforces quality and extensibility.

---

## Module Directory Structure

Each module lives in the repository:

```
terraform-modules/
├── resource-group/
│   ├── main.tf              (resource definitions)
│   ├── variables.tf         (input variable declarations)
│   ├── outputs.tf           (output value definitions)
│   ├── module.yaml          (product metadata + schema)
│   ├── schema.json          (validation rules - auto-generated or manual)
│   └── README.md            (documentation)
├── storage-account/
│   ├── main.tf
│   ├── variables.tf
│   ├── outputs.tf
│   ├── module.yaml
│   ├── schema.json
│   └── README.md
└── README.md                (modules overview)
```

---

## Example 1: Resource Group Module

### resource-group/main.tf

```hcl
terraform {
  required_providers {
    azurerm = {
      source  = "hashicorp/azurerm"
      version = "~> 3.0"
    }
  }
}

provider "azurerm" {
  features {}
  # Uses ARM_* environment variables for authentication
}

resource "azurerm_resource_group" "this" {
  name     = var.resource_group_name
  location = var.location

  tags = {
    managed_by = "azselfservice"
    module     = "resource-group"
    version    = "1.0.0"
  }
}
```

### resource-group/variables.tf

```hcl
variable "resource_group_name" {
  description = "Name of the Azure Resource Group"
  type        = string
  
  validation {
    condition     = can(regex("^[a-zA-Z0-9-]{1,90}$", var.resource_group_name))
    error_message = "Resource group name must be 1-90 characters, alphanumeric and hyphens only."
  }
}

variable "location" {
  description = "Azure region for the Resource Group"
  type        = string
  default     = "eastus"
  
  validation {
    condition     = contains(["eastus", "westus", "westeurope", "southeastasia"], var.location)
    error_message = "Location must be one of: eastus, westus, westeurope, southeastasia"
  }
}
```

### resource-group/outputs.tf

```hcl
output "resource_group_id" {
  description = "The ID of the created Resource Group"
  value       = azurerm_resource_group.this.id
}

output "resource_group_name" {
  description = "The name of the created Resource Group"
  value       = azurerm_resource_group.this.name
}

output "location" {
  description = "The location of the Resource Group"
  value       = azurerm_resource_group.this.location
}
```

### resource-group/module.yaml

```yaml
# Module Metadata
name: resource-group
version: 1.0.0
description: "Create an Azure Resource Group in a specified region"
enabled: true
terraform_path: ./terraform-modules/resource-group

# Input Variables Schema
variables:
  - name: resource_group_name
    type: string
    required: true
    description: "Name of the Resource Group (alphanumeric + dash, 1-90 chars)"
    validation:
      pattern: "^[a-zA-Z0-9-]{1,90}$"
      error_message: "Must be alphanumeric + dash, 1-90 characters"
  
  - name: location
    type: string
    required: false
    default: "eastus"
    description: "Azure region (e.g., eastus, westus)"
    enum:
      - eastus
      - westus
      - westeurope
      - southeastasia

# Output Values
outputs:
  - name: resource_group_id
    description: "The Azure resource ID of the created Resource Group"
  - name: resource_group_name
    description: "The name of the created Resource Group"
  - name: location
    description: "The location of the Resource Group"

# UI Definition (Form Layout)
ui_schema:
  layout: vertical
  fields:
    - name: resource_group_name
      label: "Resource Group Name"
      component: input
      type: text
      required: true
      placeholder: "my-rg"
      helpText: "Use lowercase letters, numbers, and hyphens. Max 90 characters."
      validationHint: "Example: contoso-prod-rg"
    
    - name: location
      label: "Azure Region"
      component: dropdown
      required: false
      default: "eastus"
      options:
        - value: eastus
          label: "East US"
        - value: westus
          label: "West US"
        - value: westeurope
          label: "West Europe"
        - value: southeastasia
          label: "Southeast Asia"
      helpText: "Choose the Azure region where the resource group will be created"
```

### resource-group/schema.json (Auto-Generated or Manual)

```json
{
  "$schema": "http://json-schema.org/draft-07/schema#",
  "title": "Resource Group Module",
  "type": "object",
  "properties": {
    "resource_group_name": {
      "type": "string",
      "pattern": "^[a-zA-Z0-9-]{1,90}$",
      "description": "Name of the Resource Group"
    },
    "location": {
      "type": "string",
      "enum": ["eastus", "westus", "westeurope", "southeastasia"],
      "default": "eastus",
      "description": "Azure region"
    }
  },
  "required": ["resource_group_name"]
}
```

### resource-group/README.md

```markdown
# Resource Group Module

Create an Azure Resource Group in a specified region.

## Usage

```hcl
module "rg" {
  source = "./terraform-modules/resource-group"
  
  resource_group_name = "my-rg"
  location            = "eastus"
}
```

## Inputs

| Name | Type | Description | Required |
|------|------|-------------|----------|
| resource_group_name | string | Name of the resource group | Yes |
| location | string | Azure region (default: eastus) | No |

## Outputs

| Name | Description |
|------|-------------|
| resource_group_id | Azure resource ID |
| resource_group_name | Resource group name |
| location | Deployed location |

## Constraints

- Name: 1-90 characters, alphanumeric and hyphens only
- Location: Must be one of eastus, westus, westeurope, southeastasia

## Limitations

- Single region only (no multi-region support)
- No tags beyond platform defaults

## Future Enhancements

- Custom tags support
- Cost allocation tags
- Resource group policies
```

---

## Module Versioning

### Semantic Versioning

Modules follow **Semantic Versioning (Major.Minor.Patch)**:

- **1.0.0** — Initial release
- **1.0.1** — Patch: bug fix, no breaking changes
- **1.1.0** — Minor: new feature, backwards compatible
- **2.0.0** — Major: breaking change

### Example Version Progression

```
1.0.0 — Initial resource-group module (name + location)
        ↓
1.0.1 — Bug fix: validation regex improved
        ↓
1.1.0 — New feature: optional tags parameter
        ↓
2.0.0 — Breaking change: tags now required
```

### Backwards Compatibility

**Module Versioning Table:**

| Scenario | What Customer Sees | What Platform Does |
|----------|-------------------|--------------------|
| Deploy with v1.0.0 | Module v1.0.0 used | Job stored with module_id + version |
| Module updated to v1.0.1 | Existing deployments still use v1.0.0 | No change to existing deployments |
| New deployment | Can select v1.0.1 (default) | Logs which version used |
| Module deprecated (v1.0.0 disabled) | v1.1.0 becomes default | Existing v1.0.0 deployments still work; new = v1.1.0 |

**Result:** No breaking changes for customers; old deployments never affected by module updates.

---

## Input Validation Framework

### Validation Layers

#### Layer 1: Schema Validation (API)

When user submits deployment request, API validates inputs against module schema **before** creating job:

```csharp
// POST /api/deployments
public async Task<IActionResult> CreateDeployment(CreateDeploymentRequest request)
{
    var module = await db.Modules.FirstAsync(m => m.Id == request.ModuleId);
    
    // Validate inputs
    var validator = new ModuleInputValidator();
    var validationResult = validator.Validate(request.Inputs, module.Schema);
    
    if (!validationResult.IsValid) {
        return BadRequest(new {
            errors = validationResult.Errors.Select(e => new {
                field = e.PropertyName,
                message = e.ErrorMessage
            })
        });
    }
    
    // Validation passed; create deployment
    var deployment = new Deployment {
        Status = DeploymentStatus.Queued,
        Inputs = request.Inputs
    };
    await db.SaveChangesAsync();
    
    return Created($"/api/deployments/{deployment.Id}", new { id = deployment.Id });
}
```

**Result:** Only valid inputs reach the worker; Terraform never sees bad data.

#### Layer 2: Terraform Validation (Worker)

Worker runs `terraform validate`:

```bash
terraform validate
```

**Result:** Catches any module-level syntax errors (shouldn't happen in production).

#### Layer 3: Terraform Plan (Optional, Post-MVP)

Worker could run `terraform plan` to dry-run before `apply`:

```bash
terraform plan -out=plan.tfout -input=false
# Review plan
terraform apply plan.tfout
```

**Result:** Approval workflow pre-MVP deferred.

### Validation Rule Types

| Type | Example | Enforcement |
|------|---------|-------------|
| Pattern (Regex) | `^[a-zA-Z0-9-]{1,90}$` | API layer |
| Enum | `["eastus", "westus"]` | API layer |
| Type | `string`, `number`, `bool` | API layer |
| Required | `required: true` | API layer |
| Default | `default: "eastus"` | API layer (if not provided) |

---

## Module Registration & Enablement

### Registering a Module

**Platform admin task:**

1. Create module folder with Terraform code + module.yaml
2. Call admin endpoint:
   ```
   POST /api/admin/modules/register
   {
     "module_path": "./terraform-modules/resource-group"
   }
   ```

3. API:
   - Reads module.yaml
   - Validates schema
   - Inserts into modules table
   - Module now available for deployments

### Module Lifecycle

**States:**

1. **Published** (enabled=true)
   - Available for new deployments
   - Existing deployments can complete

2. **Deprecated** (enabled=false)
   - No new deployments allowed
   - Existing deployments continue
   - UI: "This module is deprecated"

3. **Archived** (marked for deletion)
   - Completely hidden
   - Existing deployments still viewable
   - Recovery possible from backup

### API Endpoints (Future/Admin)

```
GET /api/modules                    # List enabled modules
GET /api/modules/{id}               # Get module details
POST /api/admin/modules/register    # Register new module
POST /api/admin/modules/{id}/disable # Deprecate module
DELETE /api/admin/modules/{id}      # Archive module (soft delete)
```

---

## Module Framework Extensibility

### Adding Next Module: Storage Account

**Requirements:**
1. Create `terraform-modules/storage-account/` folder
2. Write Terraform code (main.tf, variables.tf, outputs.tf)
3. Write module.yaml with schema + ui_schema
4. Call `POST /api/admin/modules/register`

**Platform changes required:** NONE

**Result:** New module available immediately; no platform code changes.

### Future Modules (Roadmap)

```
✓ resource-group (v1.0.0)
├ storage-account (v1.0.0)
├ key-vault (v1.0.0)
├ virtual-network (v1.0.0)
├ app-service (v1.0.0)
└ database (PostgreSQL, MySQL, SQL Server)
```

### Design for Multi-Module Deployments (Post-MVP)

Framework supports:
- Multiple modules in one deployment (future)
- Module dependencies (e.g., storage-account requires resource-group first)
- Parallel execution (independent modules run concurrently)

**Current MVP:** Single module per deployment (simplest case).

---

## Schema Definition (JSON Schema Subset)

### Supported Types

```yaml
variables:
  - name: my_string
    type: string
    
  - name: my_number
    type: number
    
  - name: my_bool
    type: bool
    
  - name: my_list
    type: list
    items: string  # list of strings
```

### Validation Keywords

```yaml
variables:
  - name: rg_name
    type: string
    required: true
    default: null
    
    validation:
      minLength: 1
      maxLength: 90
      pattern: "^[a-zA-Z0-9-]+$"
      enum: null  # no enum for strings with pattern
```

### UI Component Types

```yaml
ui_schema:
  fields:
    - component: input         # text input
    - component: textarea      # multiline text
    - component: number        # numeric input
    - component: checkbox      # checkbox
    - component: dropdown      # dropdown / select
    - component: radio         # radio buttons
    - component: multiselect   # multi-select dropdown
```

---

## Module Best Practices

### Do's

✓ Use meaningful variable names  
✓ Add descriptions to every variable  
✓ Use sensible defaults  
✓ Add output values for important resources  
✓ Include examples in README  
✓ Version modules; don't just overwrite  
✓ Test locally before registering  

### Don'ts

✗ Don't hardcode values (use variables)  
✗ Don't use provider configuration in modules (inherit from root)  
✗ Don't create undocumented outputs  
✗ Don't break backwards compatibility without major version bump  

---

## Implementation Checklist

- [ ] Create resource-group module (main.tf, variables.tf, outputs.tf)
- [ ] Create resource-group/module.yaml
- [ ] Create resource-group/schema.json
- [ ] Create resource-group/README.md
- [ ] Implement ModuleInputValidator service
- [ ] Create POST /api/admin/modules/register endpoint
- [ ] Test: register module → appears in modules list
- [ ] Test: submit deployment with valid inputs → succeeds
- [ ] Test: submit deployment with invalid inputs → rejected with errors
- [ ] Test: Terraform execution uses inputs correctly
- [ ] Test: outputs persisted and queryable
- [ ] Create second module scaffold (storage-account) to test extensibility

