# AzSelfService Platform — Database Design

## Overview

This document defines the complete data model for the platform, including all tables, relationships, indexes, and migration strategy.

---

## Technology Choice: PostgreSQL

### Why PostgreSQL (Not Cosmos DB or SQL Server)?

#### PostgreSQL vs. Cosmos DB

**Cosmos (Document database):**
- Pro: Flexible schema, JSON-friendly
- Con: Lacks relational integrity; audit chains are hard; queries across relationships are complex

**PostgreSQL (Relational database):**
- Pro: Strong ACID guarantees; enforces data integrity; supports complex joins; excellent JSON support (JSONB)
- Verdict: Better fit for our workload

#### PostgreSQL vs. SQL Server

**SQL Server:**
- Pro: Enterprise, robust
- Con: More expensive; JSON support less mature; Terraform ecosystem prefers PostgreSQL

**PostgreSQL:**
- Pro: Cheaper, better Terraform integration, cleaner JSON; works locally via Docker
- Verdict: Optimal choice for cost + functionality

---

## Core Data Model

### Customers Table

```sql
CREATE TABLE customers (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  name VARCHAR(255) NOT NULL,
  subscription_id VARCHAR(255) NOT NULL UNIQUE,
  tenant_id VARCHAR(255) NOT NULL,
  created_at TIMESTAMP DEFAULT NOW(),
  updated_at TIMESTAMP DEFAULT NOW()
);
```

**Semantics:**
- `id` — Unique identifier (UUID)
- `name` — Customer company name (e.g., "Contoso Corp")
- `subscription_id` — Azure subscription ID (GUID); maps to customer's Azure environment
- `tenant_id` — Azure Entra ID tenant ID; identifies Azure directory for customer's identities
- `created_at`, `updated_at` — Timestamps for audit

**Cardinality:** One per customer

---

### Users Table

```sql
CREATE TABLE users (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  customer_id UUID NOT NULL REFERENCES customers(id) ON DELETE CASCADE,
  username VARCHAR(255) NOT NULL UNIQUE,
  password_hash VARCHAR(255) NOT NULL,
  email VARCHAR(255),
  is_active BOOLEAN DEFAULT true,
  created_at TIMESTAMP DEFAULT NOW(),
  updated_at TIMESTAMP DEFAULT NOW()
);

CREATE INDEX idx_users_customer_id ON users(customer_id);
CREATE INDEX idx_users_username ON users(username);
```

**Semantics:**
- `id` — Unique identifier
- `customer_id` — Foreign key to customers; enforces multi-tenancy
- `username` — Login name (globally unique; no two users across all customers)
- `password_hash` — bcrypt hash (never plaintext)
- `email` — For notifications (post-MVP)
- `is_active` — Soft delete flag; false = deactivated
- `created_at`, `updated_at` — Timestamps

**Cardinality:** Many users per customer

**Usage:** Authentication, authorization, audit trail

---

### Modules Table

```sql
CREATE TABLE modules (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  name VARCHAR(255) NOT NULL UNIQUE,
  version VARCHAR(50) NOT NULL,
  terraform_path VARCHAR(1000) NOT NULL,
  schema JSONB NOT NULL,
  ui_schema JSONB,
  enabled BOOLEAN DEFAULT true,
  created_at TIMESTAMP DEFAULT NOW(),
  updated_at TIMESTAMP DEFAULT NOW(),
  UNIQUE(name, version)
);

CREATE INDEX idx_modules_enabled ON modules(enabled);
```

**Semantics:**
- `id` — Unique identifier
- `name` — Module name (e.g., "resource-group", "storage-account")
- `version` — Semantic version (e.g., "1.0.0", "1.1.0")
- `terraform_path` — Path to module in repo (e.g., "./terraform-modules/resource-group")
- `schema` — JSON schema defining variables, types, validation rules (see module-framework.md)
- `ui_schema` — JSON defining form layout (component types, labels, help text)
- `enabled` — false = deprecated/archived (blocks new deployments, allows existing)
- `created_at`, `updated_at` — Timestamps

**Cardinality:** One per unique (name, version) pair

**Example Record:**
```json
{
  "id": "xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx",
  "name": "resource-group",
  "version": "1.0.0",
  "terraform_path": "./terraform-modules/resource-group",
  "schema": {
    "variables": [
      {
        "name": "resource_group_name",
        "type": "string",
        "required": true,
        "validation": { "pattern": "^[a-zA-Z0-9-]{1,90}$" }
      },
      {
        "name": "location",
        "type": "string",
        "default": "eastus",
        "enum": ["eastus", "westus", "westeurope"]
      }
    ]
  },
  "ui_schema": {
    "fields": [
      { "name": "resource_group_name", "component": "input", "label": "Name" },
      { "name": "location", "component": "dropdown", "label": "Location" }
    ]
  },
  "enabled": true
}
```

---

### Deployments Table

```sql
CREATE TABLE deployments (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  customer_id UUID NOT NULL REFERENCES customers(id) ON DELETE CASCADE,
  module_id UUID NOT NULL REFERENCES modules(id),
  status VARCHAR(50) NOT NULL DEFAULT 'QUEUED',
  requested_by UUID NOT NULL REFERENCES users(id),
  requested_at TIMESTAMP DEFAULT NOW(),
  started_at TIMESTAMP,
  completed_at TIMESTAMP,
  terraform_state_path VARCHAR(1000),
  error_message TEXT,
  retry_count INT DEFAULT 0,
  created_at TIMESTAMP DEFAULT NOW(),
  updated_at TIMESTAMP DEFAULT NOW()
);

CREATE INDEX idx_deployments_customer_id ON deployments(customer_id);
CREATE INDEX idx_deployments_status ON deployments(status);
CREATE INDEX idx_deployments_created_at ON deployments(created_at DESC);
CREATE INDEX idx_deployments_customer_created ON deployments(customer_id, created_at DESC);
```

**Semantics:**
- `id` — Unique deployment identifier
- `customer_id` — Foreign key; which customer owns this deployment
- `module_id` — Foreign key; which module is being deployed
- `status` — Enum: QUEUED, RUNNING, SUCCEEDED, FAILED, CANCELLED
- `requested_by` — Foreign key to users; who submitted the request
- `requested_at` — Timestamp of submission
- `started_at` — Timestamp when worker started execution
- `completed_at` — Timestamp when execution finished (success or failure)
- `terraform_state_path` — Path in Blob Storage (e.g., "customers/xxx/resource-group/deploy-001.tfstate")
- `error_message` — If FAILED, error details here
- `retry_count` — Number of retries attempted (max 3)
- `created_at`, `updated_at` — Timestamps

**Cardinality:** Many per customer, many per module

---

### Deployment Inputs Table

```sql
CREATE TABLE deployment_inputs (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  deployment_id UUID NOT NULL REFERENCES deployments(id) ON DELETE CASCADE,
  payload JSONB NOT NULL,
  created_at TIMESTAMP DEFAULT NOW()
);

CREATE INDEX idx_deployment_inputs_deployment_id ON deployment_inputs(deployment_id);
```

**Semantics:**
- `id` — Unique identifier
- `deployment_id` — Foreign key to deployments (one-to-one relationship)
- `payload` — JSON object with user-provided inputs (e.g., `{"resource_group_name": "my-rg", "location": "eastus"}`)
- `created_at` — Timestamp

**Example Payload:**
```json
{
  "resource_group_name": "contoso-prod-rg",
  "location": "eastus"
}
```

**Cardinality:** One per deployment (one-to-one)

---

### Deployment Outputs Table

```sql
CREATE TABLE deployment_outputs (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  deployment_id UUID NOT NULL REFERENCES deployments(id) ON DELETE CASCADE,
  payload JSONB,
  created_at TIMESTAMP DEFAULT NOW()
);

CREATE INDEX idx_deployment_outputs_deployment_id ON deployment_outputs(deployment_id);
```

**Semantics:**
- `id` — Unique identifier
- `deployment_id` — Foreign key to deployments (one-to-one)
- `payload` — JSON object with Terraform outputs (e.g., `{"resource_group_id": "/subscriptions/.../resourceGroups/my-rg"}`)
- `created_at` — Timestamp

**Example Payload:**
```json
{
  "resource_group_id": "/subscriptions/xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx/resourceGroups/contoso-prod-rg",
  "resource_group_name": "contoso-prod-rg",
  "location": "eastus"
}
```

**Cardinality:** One per deployment (one-to-one); NULL if deployment failed

---

### Deployment Logs Table

```sql
CREATE TABLE deployment_logs (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  deployment_id UUID NOT NULL REFERENCES deployments(id) ON DELETE CASCADE,
  timestamp TIMESTAMP NOT NULL,
  level VARCHAR(20) NOT NULL,
  message TEXT NOT NULL,
  created_at TIMESTAMP DEFAULT NOW()
);

CREATE INDEX idx_deployment_logs_deployment_id_timestamp ON deployment_logs(deployment_id, timestamp DESC);
```

**Semantics:**
- `id` — Unique identifier
- `deployment_id` — Foreign key to deployments
- `timestamp` — When log was generated
- `level` — Enum: INFO, WARN, ERROR
- `message` — Log message text
- `created_at` — When log was inserted into DB

**Example Logs:**
```
deployment_id = xxx-xxx-xxx
timestamp = 2026-05-11T14:22:12Z, level = INFO, message = "Downloading module: resource-group@1.0.0"
timestamp = 2026-05-11T14:22:13Z, level = INFO, message = "terraform init"
timestamp = 2026-05-11T14:22:15Z, level = INFO, message = "azurerm_resource_group.this: Creating..."
timestamp = 2026-05-11T14:22:17Z, level = INFO, message = "azurerm_resource_group.this: Creation complete after 2s"
timestamp = 2026-05-11T14:22:17Z, level = INFO, message = "terraform apply succeeded"
```

**Cardinality:** Many per deployment (0–1000+ logs per deployment)

---

### Audit Logs Table

```sql
CREATE TABLE audit_logs (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  action VARCHAR(100) NOT NULL,
  actor UUID REFERENCES users(id),
  resource_type VARCHAR(100) NOT NULL,
  resource_id VARCHAR(255) NOT NULL,
  before JSONB,
  after JSONB,
  timestamp TIMESTAMP DEFAULT NOW(),
  created_at TIMESTAMP DEFAULT NOW()
);

CREATE INDEX idx_audit_logs_timestamp ON audit_logs(timestamp DESC);
CREATE INDEX idx_audit_logs_actor ON audit_logs(actor);
CREATE INDEX idx_audit_logs_resource ON audit_logs(resource_type, resource_id);
```

**Semantics:**
- `id` — Unique identifier
- `action` — Enum: LOGIN, DEPLOY_SUBMIT, DEPLOY_STATUS_CHANGE, CREATE, UPDATE, DELETE
- `actor` — Foreign key to users (who performed action); nullable for system actions
- `resource_type` — Enum: user, deployment, module, customer
- `resource_id` — ID of resource (can be UUID or string)
- `before` — State before change (JSON); NULL for CREATE actions
- `after` — State after change (JSON); NULL for DELETE actions
- `timestamp` — When action occurred
- `created_at` — When logged

**Example Entries:**
```
action = LOGIN
actor = user-123
resource_type = user
resource_id = user-123
timestamp = 2026-05-11T14:20:00Z

action = DEPLOY_SUBMIT
actor = user-123
resource_type = deployment
resource_id = deploy-001
after = { "status": "QUEUED", "module_id": "mod-rg", "inputs": {...} }
timestamp = 2026-05-11T14:22:10Z

action = DEPLOY_STATUS_CHANGE
actor = NULL (system)
resource_type = deployment
resource_id = deploy-001
before = { "status": "QUEUED" }
after = { "status": "RUNNING" }
timestamp = 2026-05-11T14:22:12Z

action = DEPLOY_STATUS_CHANGE
actor = NULL (system)
resource_type = deployment
resource_id = deploy-001
before = { "status": "RUNNING" }
after = { "status": "SUCCEEDED" }
timestamp = 2026-05-11T14:22:20Z
```

**Cardinality:** Many (one per significant action)

---

## Entity-Relationship Diagram

```
customers
    ├─ 1:N ─→ users
    └─ 1:N ─→ deployments

modules
    └─ 1:N ─→ deployments

deployments
    ├─ 1:1 ─→ deployment_inputs
    ├─ 1:1 ─→ deployment_outputs
    ├─ 1:N ─→ deployment_logs
    └─ M:1 ─→ users (requested_by)

users
    └─ 1:N ─→ audit_logs (as actor)

audit_logs
    └─ (resource_type, resource_id) can reference any resource
```

---

## Indexing Strategy

### Hot Queries (Most Frequent)

1. **List customer's deployments:**
   ```sql
   SELECT * FROM deployments 
   WHERE customer_id = ? 
   ORDER BY created_at DESC 
   LIMIT 20;
   ```
   **Index:** `idx_deployments_customer_created`

2. **Get recent logs for deployment:**
   ```sql
   SELECT * FROM deployment_logs 
   WHERE deployment_id = ? 
   ORDER BY timestamp DESC 
   LIMIT 100;
   ```
   **Index:** `idx_deployment_logs_deployment_id_timestamp`

3. **Poll for QUEUED jobs:**
   ```sql
   SELECT * FROM deployments 
   WHERE status = 'QUEUED' 
   ORDER BY created_at ASC 
   LIMIT 10;
   ```
   **Index:** `idx_deployments_status`

4. **Audit query:**
   ```sql
   SELECT * FROM audit_logs 
   WHERE timestamp > ? 
   ORDER BY timestamp DESC;
   ```
   **Index:** `idx_audit_logs_timestamp`

### Index Summary

| Table | Column(s) | Reason |
|-------|-----------|--------|
| users | (customer_id) | List users for customer |
| users | (username) | Login lookup |
| deployments | (customer_id) | List customer deployments |
| deployments | (status) | Poll for QUEUED |
| deployments | (customer_id, created_at DESC) | List + sort |
| deployment_logs | (deployment_id, timestamp DESC) | Stream logs |
| deployment_outputs | (deployment_id) | Fetch outputs |
| deployment_inputs | (deployment_id) | Fetch inputs |
| audit_logs | (timestamp DESC) | Audit queries |
| audit_logs | (actor) | Find actions by user |
| audit_logs | (resource_type, resource_id) | Find actions for resource |

---

## Schema Migrations (Entity Framework)

### Migration File Structure

```
AzSelfService.Infrastructure/
├── Migrations/
│   ├── 202605110001_InitialSchema.cs
│   ├── 202605110002_AddDeploymentTables.cs
│   ├── 202605110003_AddAuditLogs.cs
│   └── 202605110001_InitialSchema.Designer.cs (auto-generated)
└── Data/
    └── AzSelfServiceDbContext.cs
```

### DbContext Definition

```csharp
public class AzSelfServiceDbContext : DbContext
{
    public DbSet<Customer> Customers { get; set; }
    public DbSet<User> Users { get; set; }
    public DbSet<Module> Modules { get; set; }
    public DbSet<Deployment> Deployments { get; set; }
    public DbSet<DeploymentInput> DeploymentInputs { get; set; }
    public DbSet<DeploymentOutput> DeploymentOutputs { get; set; }
    public DbSet<DeploymentLog> DeploymentLogs { get; set; }
    public DbSet<AuditLog> AuditLogs { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Foreign keys, indexes, constraints defined here
        modelBuilder.Entity<User>()
            .HasIndex(u => new { u.CustomerId });
        
        modelBuilder.Entity<Deployment>()
            .HasIndex(d => new { d.CustomerId, d.CreatedAt }).IsDescending(false, true);
        
        // ... more indexes
    }
}
```

### Creating a Migration

```bash
# Generate migration based on DbContext changes
dotnet ef migrations add 202605110001_InitialSchema

# Apply migration to database
dotnet ef database update
```

---

## Data Retention & Archival (Post-MVP)

### MVP: Keep Everything

- Keep all deployment records forever
- Keep all logs forever
- Keep all audit logs forever

### Post-MVP: Archival Strategy

- **Deployments:** Keep in DB indefinitely (compliance)
- **Logs:** Archive to Azure Blob Storage after 90 days (compress, reduce DB size)
- **Audit logs:** Keep in DB for 1 year, archive thereafter
- **Outputs:** Keep in DB indefinitely

---

## Backup & Recovery

### MVP: Rely on Azure Database Backup

PostgreSQL Azure Flexible Server includes:
- Automatic daily backups (7-day retention)
- Point-in-time restore (PITR)
- Manual backups via portal

### Production: Enhanced Backup

- Enable geo-redundant backups (restore in different region)
- Scheduled backup validation (restore to test DB weekly)
- Backup encryption (customer-managed keys in Key Vault)

---

## Implementation Checklist

- [ ] Create PostgreSQL Azure Flexible Server
- [ ] Create DbContext with all entities
- [ ] Create initial migration (all tables)
- [ ] Run migrations against dev database
- [ ] Add seed data (test customer, test module)
- [ ] Verify indexes created
- [ ] Test connection pooling
- [ ] Validate foreign key constraints
- [ ] Set up automated backups

