# AzSelfService Platform — Terraform Execution Strategy

## Overview

This document defines how Terraform modules are safely and auditably executed, how state is managed, how logs are streamed, and how errors are handled.

---

## Core Principle: Job Queue Pattern

### Why Not Direct Execution?

**Bad:** User submits form → API runs `terraform apply` synchronously → Returns response

**Problems:**
- Long timeout (Terraform takes 2-5 minutes; HTTP timeout)
- No retry capability (failure = entire request lost)
- Hard to audit (no job record)
- Can't scale (queue up requests)

### Better: Queue-Based Asynchronous Execution

**Good:** User submits form → API stores request in DB → Worker polls queue → Worker executes Terraform → Results persisted

**Benefits:**
- **Retryable:** Failed jobs stay in queue, can be retried
- **Auditable:** Complete job history in database
- **Scalable:** Multiple workers process jobs in parallel
- **Durable:** No data loss on API crash
- **Future-proof:** Approval workflows, scheduled deployments easy to add

---

## Deployment Job Lifecycle

```
┌─────────────────────────────────────────────────────────────────┐
│                    USER SUBMITS REQUEST                         │
├─────────────────────────────────────────────────────────────────┤
│ POST /api/deployments { module_id, customer_id, inputs }        │
└────────────────┬────────────────────────────────────────────────┘
                 │
                 ▼
┌─────────────────────────────────────────────────────────────────┐
│              API VALIDATES & STORES JOB                         │
├─────────────────────────────────────────────────────────────────┤
│ 1. Validate inputs (schema, regex, enums)                       │
│ 2. Create deployment record: status = QUEUED                    │
│ 3. Store inputs in deployment_inputs table                      │
│ 4. Return deployment_id to frontend                             │
│ 5. Frontend polls GET /api/deployments/{id} for status          │
└────────────────┬────────────────────────────────────────────────┘
                 │
                 ▼
┌─────────────────────────────────────────────────────────────────┐
│            WORKER PICKS UP JOB FROM QUEUE                       │
├─────────────────────────────────────────────────────────────────┤
│ 1. HostedService polls: SELECT * FROM deployments WHERE         │
│    status = QUEUED ORDER BY created_at LIMIT 10                 │
│ 2. For each job: set status = RUNNING, started_at = NOW()       │
│ 3. Execute Terraform in isolated container                      │
└────────────────┬────────────────────────────────────────────────┘
                 │
                 ▼
┌─────────────────────────────────────────────────────────────────┐
│          TERRAFORM EXECUTION & LOG STREAMING                    │
├─────────────────────────────────────────────────────────────────┤
│ 1. Download module from repo                                    │
│ 2. Prepare terraform.tfvars from deployment_inputs              │
│ 3. Run: terraform init -backend-config=...                      │
│ 4. Run: terraform apply -auto-approve                           │
│ 5. Stream stdout/stderr to deployment_logs table                │
│ 6. Frontend polls GET /api/deployments/{id}/logs, displays persisted logs │
└────────────────┬────────────────────────────────────────────────┘
                 │
                 ▼
┌─────────────────────────────────────────────────────────────────┐
│              TERRAFORM COMPLETES                                │
├─────────────────────────────────────────────────────────────────┤
│ SUCCESS:                                                         │
│  - Parse terraform output -json                                 │
│  - Store in deployment_outputs table                            │
│  - Update deployment: status = SUCCEEDED, completed_at = NOW()  │
│                                                                  │
│ FAILURE:                                                         │
│  - Capture stderr                                               │
│  - Store in deployment: error_message = "..."                   │
│  - Update deployment: status = FAILED, completed_at = NOW()     │
│  - Retry logic: if retries < max_retries, status = QUEUED       │
└────────────────┬────────────────────────────────────────────────┘
                 │
                 ▼
┌─────────────────────────────────────────────────────────────────┐
│              FRONTEND DISPLAYS RESULTS                          │
├─────────────────────────────────────────────────────────────────┤
│ GET /api/deployments/{id} returns:                              │
│  - status: SUCCEEDED                                            │
│  - completed_at: 2026-05-11T14:22:30Z                           │
│  - logs: [{ timestamp, level, message }, ...]                   │
│                                                                  │
│ GET /api/deployments/{id}/outputs returns:                      │
│  - resource_group_id: /subscriptions/xxx/resourceGroups/xxx     │
│  - resource_group_name: my-rg                                   │
└─────────────────────────────────────────────────────────────────┘
```

---

## Terraform State Management

### State Storage Backend

**Where:** Azure Blob Storage (managed by platform)

**Why:**
- Durable: survives worker crashes
- Isolated: one state file per deployment
- Auditable: Blob storage can track access logs
- Secure: RBAC access control

### State File Path Structure

Each deployment gets its own state file:

```
tfstate/
├── customers/
│   ├── a1b2c3d4-e5f6-47a8-9b0c-1d2e3f4a5b6c/  (customer_id)
│   │   ├── resource-group/
│   │   │   ├── deploy-001.tfstate
│   │   │   ├── deploy-002.tfstate
│   │   │   └── deploy-003.tfstate
│   │   └── storage-account/
│   │       ├── deploy-001.tfstate
│   │       └── deploy-002.tfstate
│   └── x9y8z7w6-v5u4-t3s2-r1q0-p9o8n7m6l5k4/  (another customer)
│       └── resource-group/
│           └── deploy-001.tfstate
```

**Structure Semantics:**
- `customers/{customer_id}/` — Customer isolation
- `{module_type}/` — Module organization
- `{deployment_id}.tfstate` — Individual deployment state

### Backend Configuration

Worker passes backend config to Terraform:

```hcl
# backend.tfvars (generated per deployment)
resource_group_name  = "tf-state-rg"
storage_account_name = "tfstate12345"
container_name       = "tfstate"
key                  = "customers/a1b2c3d4-e5f6-47a8-9b0c-1d2e3f4a5b6c/resource-group/deploy-001.tfstate"
```

Worker runs:
```bash
terraform init \
  -backend-config=resource_group_name=tf-state-rg \
  -backend-config=storage_account_name=tfstate12345 \
  -backend-config=container_name=tfstate \
  -backend-config=key=customers/.../deploy-001.tfstate
```

### State Locking

Terraform uses Blob Storage **lease locks** to prevent concurrent modifications:

- When `terraform apply` starts: acquires lease on state blob
- While running: other workers cannot modify same state
- On completion: releases lease
- If worker crashes: lease timeout (300 seconds) releases automatically

---

## Input → Terraform Variables

### Flow

```
User fills form:
  resource_group_name = "my-rg"
  location = "eastus"
        ↓
API stores in deployment_inputs:
  { "resource_group_name": "my-rg", "location": "eastus" }
        ↓
Worker fetches deployment_inputs as JSON
        ↓
Worker writes terraform.tfvars:
  resource_group_name = "my-rg"
  location            = "eastus"
        ↓
Terraform reads variables from terraform.tfvars
        ↓
Module uses variables:
  name     = var.resource_group_name
  location = var.location
```

### Validation Happens Before Job Creation

The API validates inputs against module schema **before** storing in database:

```csharp
// POST /api/deployments
var module = await db.Modules.FirstAsync(m => m.Id == request.ModuleId);
var validator = new ModuleInputValidator();
var validationResult = validator.Validate(request.Inputs, module.Schema);

if (!validationResult.IsValid) {
    return BadRequest(validationResult.Errors); // 400, show errors to user
}

// If valid, create deployment
var deployment = new Deployment { 
    Status = DeploymentStatus.Queued,
    Inputs = request.Inputs 
};
```

**Result:** Only valid inputs reach the worker; Terraform never sees invalid data.

---

## Terraform Outputs → Database

### Flow

```
Terraform finishes:
  output "resource_group_id" { 
    value = azurerm_resource_group.this.id 
  }
        ↓
Worker runs: terraform output -json
        ↓
Output JSON:
  {
    "resource_group_id": {
      "value": "/subscriptions/xxx/resourceGroups/my-rg"
    },
    "resource_group_name": {
      "value": "my-rg"
    }
  }
        ↓
Worker parses JSON, extracts values
        ↓
Worker stores in deployment_outputs table:
  {
    "resource_group_id": "/subscriptions/xxx/resourceGroups/my-rg",
    "resource_group_name": "my-rg"
  }
        ↓
API: GET /api/deployments/{id}/outputs
        ↓
Frontend displays outputs to user

## UI Update Model

The current implementation does not push live log events over a socket or stream. The UI reads deployment state and deployment log rows from the API and refreshes them by polling.
```

---

## Log Streaming

### Real-Time Logs

**Goal:** Customer sees logs as they happen, not just at the end.

### Implementation

1. **Worker captures Terraform output:**
   ```bash
   terraform apply -no-color -lock=true 2>&1 | tee terraform.log
   ```

2. **Worker reads log line-by-line, parses level:**
   ```
   [INFO] Downloading module...
   [INFO] terraform init
   [INFO] azurerm_resource_group.this: Creating...
   [INFO] azurerm_resource_group.this: Creation complete after 2s
   ```

3. **For each line, insert into database:**
   ```csharp
   await db.DeploymentLogs.AddAsync(new DeploymentLog {
       DeploymentId = deploymentId,
       Timestamp = DateTime.UtcNow,
       Level = "INFO",
       Message = "Downloading module..."
   });
   await db.SaveChangesAsync(); // Flush immediately
   ```

4. **Frontend polls every 2 seconds:**
   ```
   GET /api/deployments/{id}/logs?after_timestamp=2026-05-11T14:22:10Z
   ```

5. **API returns new logs:**
   ```json
   {
     "logs": [
       { "timestamp": "2026-05-11T14:22:12Z", "level": "INFO", "message": "terraform init" },
       { "timestamp": "2026-05-11T14:22:15Z", "level": "INFO", "message": "azurerm_resource_group.this: Creating..." }
     ]
   }
   ```

6. **Frontend appends logs to UI, displays in real-time**

---

## Service Principal & Authentication

### Worker Retrieves SP Credentials

```csharp
// Worker starts with Managed Identity
var kvUri = "https://{vault-name}.vault.azure.net/";
var client = new SecretClient(new Uri(kvUri), new DefaultAzureCredential());

// Read single secret and metadata from Key Vault
var clientSecret = await client.GetSecretAsync($"customers/{customerId}/sp-client-secret");
var clientId = ParseAppIdFromMetadata(clientSecret.Value.Properties.ContentType);

// Read tenant/subscription from customer metadata
var tenantId = customer.TenantId;
var subscriptionId = customer.SubscriptionId;
```

### Worker Injects Into Terraform

```csharp
// Set environment variables before running terraform
var env = new Dictionary<string, string>
{
  { "ARM_CLIENT_ID", clientId },
    { "ARM_CLIENT_SECRET", clientSecret.Value.Value },
  { "ARM_TENANT_ID", tenantId },
  { "ARM_SUBSCRIPTION_ID", subscriptionId }
};

var process = new Process {
    StartInfo = new ProcessStartInfo {
        FileName = "terraform",
        Arguments = "apply -auto-approve",
        Environment = env,
        RedirectStandardOutput = true,
        RedirectStandardError = true
    }
};
```

### Terraform Uses SP Credentials

Terraform automatically uses `ARM_*` environment variables:

```hcl
provider "azurerm" {
  features {}
  # Credentials come from ARM_CLIENT_ID, ARM_CLIENT_SECRET, etc.
}

resource "azurerm_resource_group" "this" {
  name     = var.resource_group_name
  location = var.location
}
```

**Result:** Terraform authenticates to customer's subscription as customer's SP.

---

## Error Handling & Retries

### Status Transitions

```
QUEUED → RUNNING → SUCCEEDED (happy path)
QUEUED → RUNNING → FAILED (error)
QUEUED → RUNNING → FAILED → QUEUED (retry)
```

### Failure Scenarios

#### Scenario 1: Invalid Input (Caught at API Level)

```
User submits: resource_group_name = "invalid name!" (contains space & !)
API validates against schema
API returns 400 Bad Request: "Name must be alphanumeric + dash"
User never gets a deployment ID
No job created
```

#### Scenario 2: Terraform Validation Error (Caught at Worker Level)

```
Job created, status = QUEUED
Worker executes: terraform init
Error: "backend storage account not found"
Worker captures error, stores in database
Status = FAILED, error_message = "backend storage account not found"
Retry logic: increment retry_count, status = QUEUED (retry up to 3 times)
```

#### Scenario 3: Infrastructure Error (Resource Already Exists)

```
Worker executes: terraform apply
Error: "azurerm_resource_group.this already exists"
Status = FAILED
Retry won't help (resource already exists)
User sees error, corrects input, submits new request
```

### Retry Logic

```csharp
// After deployment fails
var deployment = await db.Deployments.FirstAsync(d => d.Id == deploymentId);
if (deployment.RetryCount < 3) {
    deployment.RetryCount++;
    deployment.Status = DeploymentStatus.Queued;
    await db.SaveChangesAsync(); // Back to queue
} else {
    deployment.Status = DeploymentStatus.Failed;
    await db.SaveChangesAsync(); // Give up
}
```

### Max Retries

- **Max Retries:** 3 (configurable)
- **Retry Backoff:** Exponential (1s, 2s, 4s between retries)
- **User Visibility:** "Deployment failed after 3 attempts. Error: [message]"

---

## Database Schema (Deployment Tables)

```sql
CREATE TABLE deployments (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  customer_id UUID NOT NULL REFERENCES customers(id),
  module_id UUID NOT NULL REFERENCES modules(id),
  status VARCHAR(50) NOT NULL DEFAULT 'QUEUED', -- QUEUED, RUNNING, SUCCEEDED, FAILED, CANCELLED
  requested_by UUID NOT NULL REFERENCES users(id),
  requested_at TIMESTAMP DEFAULT NOW(),
  started_at TIMESTAMP,
  completed_at TIMESTAMP,
  terraform_state_path VARCHAR(1000), -- e.g., "customers/xxx/resource-group/deploy-001.tfstate"
  error_message TEXT,
  retry_count INT DEFAULT 0,
  created_at TIMESTAMP DEFAULT NOW(),
  updated_at TIMESTAMP DEFAULT NOW()
);

CREATE TABLE deployment_inputs (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  deployment_id UUID NOT NULL REFERENCES deployments(id) ON DELETE CASCADE,
  payload JSONB NOT NULL, -- { "resource_group_name": "my-rg", "location": "eastus" }
  created_at TIMESTAMP DEFAULT NOW()
);

CREATE TABLE deployment_outputs (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  deployment_id UUID NOT NULL REFERENCES deployments(id) ON DELETE CASCADE,
  payload JSONB, -- { "resource_group_id": "/subscriptions/.../resourceGroups/my-rg" }
  created_at TIMESTAMP DEFAULT NOW()
);

CREATE TABLE deployment_logs (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  deployment_id UUID NOT NULL REFERENCES deployments(id) ON DELETE CASCADE,
  timestamp TIMESTAMP NOT NULL,
  level VARCHAR(20) NOT NULL, -- INFO, WARN, ERROR
  message TEXT NOT NULL
);

CREATE INDEX idx_deployments_customer_id ON deployments(customer_id);
CREATE INDEX idx_deployments_status ON deployments(status);
CREATE INDEX idx_deployment_logs_deployment_id ON deployment_logs(deployment_id, timestamp DESC);
```

---

## Implementation Checklist

- [ ] Create deployments, deployment_inputs, deployment_outputs, deployment_logs tables
- [ ] Implement ModuleInputValidator service
- [ ] Create POST /api/deployments endpoint (validate, store, return ID)
- [ ] Create GET /api/deployments/{id} endpoint (return status + basic info)
- [ ] Create GET /api/deployments/{id}/logs endpoint (return logs with pagination)
- [ ] Create GET /api/deployments/{id}/outputs endpoint (return outputs)
- [ ] Implement HostedService to poll QUEUED jobs
- [ ] Implement Terraform execution worker (separate ASP.NET Worker project)
- [ ] Implement log streaming (insert to DB per line)
- [ ] Implement retry logic (max 3 retries with backoff)
- [ ] Test end-to-end: submit form → logs stream → outputs display

