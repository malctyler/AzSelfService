# AzSelfService Platform — Solution Overview

## Executive Summary

**AzSelfService** is a controlled self-service Azure provisioning platform that enables customers to deploy approved infrastructure modules without requiring direct Terraform expertise.

**What:** A web-based platform (React frontend, .NET backend) that collects infrastructure requests via forms, validates inputs, queues deployment jobs, executes Terraform modules asynchronously, and provides real-time status tracking and audit trails.

**Why:** Accelerate infrastructure delivery, reduce manual handoffs, enforce governance through approved modules, maintain full audit compliance, and empower customers with self-service capabilities while keeping platform engineers in control.

**Core Principle:** Terraform remains the source of truth for infrastructure. The platform is an orchestration layer that abstracts complexity, not an infrastructure engine.

---

## Problem Statement

### Current State Pain Points

1. **Manual Infrastructure Requests** — Customers submit tickets → Platform engineers manually execute Terraform → Days of turnaround
2. **Knowledge Silos** — Only platform engineers understand Terraform; customers cannot troubleshoot or track deployments
3. **Audit Gaps** — Manual execution makes change tracking difficult; compliance audit trails are incomplete
4. **Scalability Bottleneck** — As customer base grows, manual request handling becomes unsustainable
5. **Error-Prone Process** — Manual parameter passing, variable mistakes, inconsistent naming conventions

### Target Customer Profile

- Managed Azure services customers
- 10–100+ deployment requests per month
- Need infrastructure quickly (RGs, storage, networking, databases)
- Want self-service but require governance/approval trails
- Existing Azure subscriptions, service principals, security policies

### Success Metrics

- Customer can deploy Resource Group in <5 minutes via UI
- Full audit trail (who, what, when, why)
- Zero manual intervention in deployment flow
- 99% deployment success rate (excluding invalid inputs)
- Extensible to N modules without platform code changes

---

## Architectural Philosophy

### Core Principles

#### 1. Terraform Remains Source of Truth

**Never let the frontend become the infrastructure engine.**

- Terraform code: owns all infrastructure logic, state, lifecycle
- Platform: collects intent, validates inputs, orchestrates workflow
- Separation of concerns: frontend is UX; backend is orchestration; Terraform is execution

**Implication:** If you want to change infrastructure behavior, you modify Terraform modules, not platform logic.

#### 2. Every Deployment Is a Tracked Job

**Do not directly run Terraform from a form submit.**

Flow:
1. User submits request → API stores in database (status=QUEUED)
2. Backend creates deployment job
3. Worker process polls job queue
4. Worker executes Terraform asynchronously
5. Logs, state, results persisted to database

**Benefits:**
- Retryable: failed deployments can be retried without re-submitting the form
- Auditable: every job has a complete history (who requested, when, what inputs, what outputs, what failed)
- Future-proof: queuing enables approvals, concurrency control, scheduled deployments
- Safe: worker crashes don't lose request data

#### 3. Modules Are Products

Each approved Terraform module should be treated as a product:

- Versioned (e.g., 1.0.0, 1.1.0, 2.0.0)
- Documented (README, schema, validation rules)
- Self-describing (metadata, form layout, outputs)
- Enabled/disabled (lifecycle control)
- Tested (local validation before deployment)

**Implication:** Adding a new module (e.g., Storage Account) requires:
- Terraform code + module.yaml metadata
- NO platform code changes
- Platform scales naturally

#### 4. Customer Isolation by Design

- One state file per deployment instance (not global)
- Customers cannot see each other's deployments
- Each customer has separate Service Principal credentials (stored in Key Vault)
- Database queries filtered by customer_id at all levels

**Implication:** Multi-tenancy is not a feature; it's a constraint enforced everywhere.

---

## Target Customers & Use Cases

### Use Case 1: Self-Service Resource Group Creation

**Actor:** Customer DevOps engineer

**Flow:**
1. Log in to platform
2. Navigate to "New Deployment"
3. Select "Resource Group" module
4. Fill form: RG name (e.g., "prod-platform-rg"), location (e.g., "eastus")
5. Click "Deploy"
6. Platform: validates name format, checks availability, queues job
7. Worker: executes Terraform module, creates RG in customer's subscription
8. Customer: sees status "Creating" → "Succeeded" with outputs (RG ID, resource URL)
9. Customer: can view full audit trail (who requested, when, what inputs)

### Use Case 2: Multi-Module Workflow (Future)

**Actor:** Platform engineer designing infrastructure for multiple customers

**Flow:**
1. Customer A: "Create RG for prod environment"
2. Customer B: "Create RG for dev environment"
3. Customer A: "Create storage account in prod RG"
4. Customer B: "Create storage account in dev RG"

Platform handles all 4 in parallel (job queue), each isolated, each audited.

### Use Case 3: Troubleshooting & Rollback (Future)

**Actor:** Customer or platform engineer investigating deployment failure

**Flow:**
1. Deployment shows status "FAILED"
2. View logs: see exact Terraform error (e.g., "RG name already exists")
3. Correct inputs, click "Retry"
4. Platform: re-runs deployment with new inputs
5. View before/after in audit log

---

## MVP Scope

### Included in MVP

**Authentication & User Management:**
- Local username/password authentication
- JWT token-based session management
- Multi-tenant user model (users belong to customers)

**Customer Management:**
- Manual admin entry (platform admin creates customer record)
- Stores: customer name, Azure subscription ID, tenant ID

**Single Module:**
- **Resource Group** — create Azure Resource Group with name + location

**Deployment Workflow:**
- Form submission (collect RG name, location)
- Input validation (name format, location enum)
- Job queuing (asynchronous execution)
- Terraform execution via worker
- Real-time log streaming to UI
- Output display (RG ID, resource group URL)

**Audit & Visibility:**
- All API requests logged
- Deployment state changes tracked
- User actions recorded (login, deployment submission, output view)
- Audit API (`GET /api/audit`) with filtering
- Audit UI page

**State Management:**
- Terraform state stored in Azure Blob Storage
- One state file per deployment instance
- Service Principal credentials in Azure Key Vault

---

## Out of Scope (Design For, Don't Build)

### Explicitly Excluded from MVP

- **Multi-cloud support** (AWS, GCP)
- **Approval workflows** (request → review → approve → execute)
- **RBAC inheritance** (nested roles, permission delegation)
- **Terraform plan visualization** (plan diff viewer)
- **Drift detection** (detect out-of-band infrastructure changes)
- **Rollback UI** (automatic rollback on failure)
- **Dependency orchestration** (module A must complete before module B)
- **Terraform cost estimation** (pre-deployment cost forecast)
- **Module vendor portal** (self-service module registration)
- **Multi-request deployments** (create 5 resources in one request)

### Design For (But Don't Build)

These should be architecturally possible without redesigning the platform:

- Adding new modules (Storage Account, Virtual Network, etc.)
- Scaling workers (multiple Terraform executors)
- Approval chains
- Scheduled/recurring deployments
- Terraform plan review before apply
- Blast radius analysis

---

## Customer Journey — Happy Path

### Day 1: Platform Admin Setup

1. Platform admin creates customer record in DB
   - Name: "Contoso Corp"
   - Subscription ID: `xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx`
   - Tenant ID: `yyyyyyyy-yyyy-yyyy-yyyy-yyyyyyyyyyyy`

2. Platform admin uploads customer's Service Principal credentials to Azure Key Vault

3. Platform admin invites customer user (e.g., devops@contoso.com)

### Day 2: Customer Uses Platform

1. **Login**
   - Navigate to https://azselfservice.example.com
   - Enter username / password
   - Receive JWT token, redirected to dashboard

2. **View History**
   - Dashboard shows: 0 deployments (first time)

3. **Create Resource Group**
   - Click "New Deployment"
   - Select "Resource Group" from dropdown
   - Fill form:
     - Name: `contoso-prod-rg` (validated: alphanumeric, dash, 1-90 chars)
     - Location: `eastus` (dropdown: eastus, westus, westeurope, southeastasia)
   - Review: "Create resource group 'contoso-prod-rg' in region 'eastus'"
   - Click "Deploy"

4. **Live Monitoring**
   - Status: QUEUED → RUNNING
   - Logs stream in real-time:
     ```
     [INFO] Downloading module: resource-group@1.0.0
     [INFO] terraform init
     [INFO] terraform apply -auto-approve
     [INFO] azurerm_resource_group.this: Creating...
     [INFO] azurerm_resource_group.this: Creation complete after 2s
     ```
   - Status: SUCCEEDED

5. **View Outputs**
   - Display outputs:
     ```
     Resource Group ID: /subscriptions/xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx/resourceGroups/contoso-prod-rg
     Resource Group Name: contoso-prod-rg
     Location: eastus
     ```

6. **View Audit Trail**
   - Navigate to "Audit Log"
   - Filter: Last 24 hours
   - See entries:
     ```
     [2026-05-11 14:22:10] User: devops@contoso.com | Action: DEPLOY_SUBMIT | Module: resource-group | Status: ✓
     [2026-05-11 14:22:12] Deployment ID: xxx-xxx-xxx | Status: QUEUED → RUNNING
     [2026-05-11 14:22:15] Deployment ID: xxx-xxx-xxx | Status: RUNNING → SUCCEEDED
     ```

---

## Success Criteria

By end of MVP, the platform should:

1. ✅ Zero manual Terraform execution (all via queue)
2. ✅ Customer can deploy RG in <5 minutes
3. ✅ Full audit trail for compliance
4. ✅ Real-time status visibility
5. ✅ Service Principal never exposed to frontend
6. ✅ Customer isolation enforced at DB level
7. ✅ Platform scalable to next module (Storage Account) with no platform code changes

---

## Technical Stack (Summary)

| Component | Choice | Why |
|-----------|--------|-----|
| Frontend | React + Next.js + TypeScript | Enterprise-grade, Azure SDK integration, Copilot-friendly |
| Backend | ASP.NET Core (.NET 9) | Azure SDK excellence, Terraform orchestration, maintainability |
| Database | PostgreSQL | Relational integrity, JSONB support, migrations |
| Auth (MVP) | Local provider | Simple, no external dependencies |
| Terraform Execution | Job queue + worker | Safe, auditable, retryable |
| State Storage | Azure Blob Storage | Customer isolation, secure |
| Hosting | Azure Static Web Apps (frontend), Azure App Service (backend) | Managed, scales, costs predictable |

---

## Next Steps

1. **Phase 1:** Complete architecture documentation (this document + auth-model, terraform-execution, database-design, module-framework)
2. **Phase 2:** Containerized dev environment (.devcontainer, docker-compose, Dockerfile)
3. **Phase 3:** Core Terraform execution platform (job queue + worker)
4. **Phase 4:** Module registry & validation
5. **Phase 5:** Frontend UI
6. **Phase 6:** Audit & history views
7. **Phase 7:** Security hardening (pre-prod)
8. **Phase 8:** Network infrastructure & production deployment

