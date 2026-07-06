# Development Guide

This guide covers local development setup and workflows for AzSelfService.

## Table of Contents

1. [Quick Start](#quick-start)
2. [Architecture](#architecture)
3. [Development Workflow](#development-workflow)
4. [Debugging](#debugging)
5. [Testing](#testing)
6. [Common Tasks](#common-tasks)
7. [Troubleshooting](#troubleshooting)

## Quick Start

### Prerequisites

#### ✅ Required

- **Docker Desktop** (Windows/Mac) or Docker Engine (Linux)
  - Includes Docker CLI and Docker Compose
  - Download: https://www.docker.com/products/docker-desktop
  - Verify: `docker --version` and `docker-compose --version`

- **Git**
  - For version control
  - Download: https://git-scm.com/downloads
  - Windows users: Git for Windows includes Git Bash

- **VS Code** (recommended, not required)
  - Any code editor works, but VS Code has C# and Node extensions
  - Download: https://code.visualstudio.com

#### ❌ Do NOT Install Locally

- **Terraform CLI** - The Worker service (inside docker-compose) handles all Terraform execution. This keeps state isolated and secure. Developers never run `terraform apply` locally.

- **Azure CLI** - Service principals are retrieved from Azure Key Vault by the Worker service. Developers don't authenticate to Azure locally.

- **.NET SDK** - Backend runs in a container. Only needed if you want to debug locally (advanced).

- **Node.js** - Frontend runs in a container via `npm install` and `npm run dev`. Only needed if you want to debug locally (advanced).

#### 🟡 Optional Conveniences

- **Make** (Windows: via Git Bash, WSL2, or `choco install make`)
  - Used to simplify docker-compose commands (e.g., `make up` instead of `docker-compose up`)
  - If you don't have Make, just run docker-compose commands directly
  - Example: `docker-compose up` instead of `make up`

- **.NET SDK 9** (only if you want local debugging of backend code)
  - Download: https://dotnet.microsoft.com/download/dotnet
  - Not required for regular development

- **Node.js 20** (only if you want local debugging of frontend code)
  - Download: https://nodejs.org
  - Not required for regular development

#### Why This Architecture?

- **Containers isolate dependencies:** Developers don't need exact versions installed locally
- **Terraform in Worker:** State management is centralized; prevents conflicts and mistakes
- **Azure auth via Key Vault:** Secure pattern; developers never commit credentials
- **docker-compose:** Single source of truth for the entire stack

### First Time Setup

```bash
# Clone the repository
git clone <repository-url>
cd AzSelfService

# Setup development environment
make setup

# Start all services
make up

# Wait 30-60 seconds for initialization...
# Then access:
#   Frontend:  http://localhost:3000
#   API Docs:  http://localhost:5000/swagger
#   Login:     admin / Test@1234
```

### Windows (Docker Desktop + PowerShell)

If you are on Windows, the repo includes native PowerShell scripts that work directly with Docker Desktop (no WSL/Git Bash required):

```powershell
# Run from repository root
.\scripts\dev-setup.ps1
.\scripts\dev-up.ps1

# Later, stop services
.\scripts\dev-down.ps1
```

If startup fails, ensure Docker Desktop is running and that `docker compose version` succeeds in PowerShell.

### Development Workflow: Choose Your Approach

#### Approach 1: Docker Compose + Terminal (Recommended, Simplest)

```bash
# Start all services
make up          # or: docker-compose up

# In another terminal, access containers as needed:
make logs-api    # Watch backend logs
make shell-db    # SSH into database container
docker exec -it azselfservice-backend bash  # Access backend container
```

**Why this approach:**
- ✅ No extraction issues or complex setup
- ✅ Full control over services
- ✅ Easy to see logs and debug
- ✅ All services containerized; nothing local to manage

#### Approach 2: VS Code Dev Container (Optional)

Dev Container runs VS Code inside a container for a fully isolated coding environment.

```bash
# Install extension: "Dev Containers" by Microsoft
# Then: File > Open Folder > AzSelfService
# Press Ctrl+Shift+P and run: "Dev Containers: Reopen in Container"
# Wait for setup to complete, then:
make up         # Start backend services
```

**Why use this:**
- Fully containerized development
- Team consistency (everyone has identical environment)
- Better for onboarding multiple developers

**Why skip this (for solo dev now):**
- Extra complexity with no added benefit for one developer
- Docker Desktop extraction issues on Windows are common
- Approach 1 (docker-compose) is faster to get started

## Architecture

### Services

The development environment includes 4 services:

| Service | Purpose | Port | Tech |
|---------|---------|------|------|
| **PostgreSQL** | Data persistence | 5432 | PostgreSQL 15 |
| **Backend API** | Business logic & Terraform orchestration | 5000 | ASP.NET Core 9 |
| **Frontend** | Web UI | 3000 | Next.js + React |
| **Worker** | Terraform execution (placeholder MVP) | 5001 | ASP.NET Core 9 |

### Data Flow

```
User Interface (http://localhost:3000)
    ↓
Backend API (http://localhost:5000)
    ↓
PostgreSQL (localhost:5432)
    ↓ (async job queue)
Worker (http://localhost:5001)
    ↓ (via Terraform)
Azure Subscription
```

### Directory Structure

```
AzSelfService/
├── .devcontainer/          # VS Code Dev Container config
│   ├── devcontainer.json
│   └── post-create.sh      # Setup script
├── backend/                # ASP.NET Core projects (Phase 2b)
│   ├── AzSelfService.API/
│   ├── AzSelfService.Core/
│   ├── AzSelfService.Infrastructure/
│   └── AzSelfService.Worker/
├── frontend/               # Next.js project (Phase 2b)
│   ├── src/
│   └── public/
├── terraform-modules/      # Terraform modules (Phase 2b+)
│   └── resource-group/
├── docs/                   # Architecture & decisions
│   ├── architecture/
│   └── adr/
├── scripts/                # Development scripts
│   ├── dev-setup.sh
│   ├── dev-up.sh
│   ├── dev-down.sh
│   └── init-db.sh
├── logs/                   # Application logs (created by setup)
├── docker-compose.yml      # Service orchestration
├── Dockerfile.dev          # Multi-stage build
├── .env                    # Local environment config (git-ignored)
├── .env.example            # Config template
├── Makefile                # Convenient commands
└── README.md               # Project overview
```

## Development Workflow

### Azure Bootstrap for Secure Secrets

When preparing a real Azure environment, use the PowerShell helpers in `scripts/` instead of writing secrets into `.env` files or the database.

```powershell
# Create the bootstrap RG and RBAC-enabled Key Vault
.\scripts\bootstrap-platform-secrets-storage.ps1 `
  -CredentialClixmlPath 'C:\Users\malcolm.COTTAGES\CredentialStore\PROD-Automation.clixml' `
  -TenantId 'bf0465f4-f8c0-4ff4-978d-af5315afa795' `
  -SubscriptionId '5b337264-50ba-4056-bc9f-1a926a433c18' `
  -ResourceGroupName 'rg-azselfservice-bootstrap' `
  -KeyVaultName 'azselfservicebootstrapkv' `
  -StorageAccountName 'azselfservicetfstate01' `
  -SoftwareStorageAccountName 'azselfservicesoftware01' `
  -SecretName 'starting-secret'

# Point the platform customer record at the client-secret Key Vault reference
.\scripts\set-customer-keyvault-refs.ps1 `
  -SubscriptionId '5b337264-50ba-4056-bc9f-1a926a433c18' `
  -KeyVaultName 'azselfservicebootstrapkv'
```

Populate this customer secret in Key Vault before allowing deployments:

- `customers/{customer_id}/sp-client-secret`

Set the secret content type to include the SP app id:

- `appid={service-principal-app-id}`

Tenant and subscription are read from customer metadata (`tenant_id`, `subscription_id`).

For repeatable local setup using CLIXML and `.env`, see:

- [Local Key Vault Development Runbook](docs/architecture/local-keyvault-dev-runbook.md)

### Software Package Library

The software storage account (`azselfservicesoftware01`) and `packages` container are used for installable package archives.

- Convention doc: [Software Package Convention](docs/architecture/software-package-convention.md)

Create a package zip from an installer:

```powershell
.\scripts\new-software-package.ps1 `
  -PackageId 'igorpavlov.7zip' `
  -VendorSlug 'igorpavlov' `
  -ProductSlug '7zip' `
  -Version '24.09.0' `
  -InstallerPath '.\software-packages\downloads\7z2409-x64.msi' `
  -InstallerType 'msi' `
  -DetectPath 'C:\Program Files\7-Zip\7z.exe' `
  -Publisher 'Igor Pavlov' `
  -DisplayName '7-Zip'
```

Publish the zip to the package catalog:

```powershell
.\scripts\publish-software-package.ps1 `
  -ZipPath '.\software-packages\igorpavlov-7zip-24.09.0-windows-x64-msi.zip' `
  -PackageId 'igorpavlov.7zip' `
  -Version '24.09.0' `
  -StorageAccountName 'azselfservicesoftware01' `
  -ContainerName 'packages' `
  -Scope 'platform'
```

### Device Move Recovery

If you switch machines, use the rehydration script to recreate the CLIXML credential, refresh `.env`, upload the customer secret, and patch the active customer row.

```powershell
.\scripts\repair-dev-machine.ps1 `
  -ServicePrincipalAppId '<appid>' `
  -ServicePrincipalSecret '<secret>' `
  -TenantId '<tenant-guid>' `
  -SubscriptionId '<subscription-guid>'
```

By default, the script uses `$HOME\CredentialStore\PROD-Automation.clixml`.

Override `-CustomerSecretRef` if you want a different Key Vault secret name than `starting-secret`.

### Starting Development

```bash
# One time: setup environment
make setup

# Every day: start services
make up

# View logs if needed
make logs

# When done: stop services (data persists)
make down
```

### Making Changes

#### Backend API Changes

1. Edit code in `backend/AzSelfService.API/` or other projects
2. Rebuild container: `docker-compose build backend`
3. Restart service: `make restart` or `docker-compose up -d backend`

#### Frontend Changes

1. Edit code in `frontend/src/`
2. Next.js hot-reload is enabled in dev mode - changes auto-apply
3. If build fails, restart: `docker-compose up -d frontend`

#### Database Schema Changes

1. Create Entity Framework Core migration: `dotnet ef migrations add <name>`
2. Update-database runs on API startup
3. Changes automatically applied when container starts

#### Adding New Modules

1. Create new folder: `terraform-modules/new-module/`
2. Add: `main.tf`, `variables.tf`, `outputs.tf`, `module.yaml`
3. Register in database via API (Module endpoint)
4. No platform code changes needed ✓

### Testing Locally

```bash
# View API documentation
# Open: http://localhost:5000/swagger

# Test login endpoint
curl -X POST http://localhost:5000/api/auth/login \
  -H "Content-Type: application/json" \
  -d '{"username":"admin","password":"Test@1234"}'

# Get JWT token from response, then use for other calls
TOKEN="your-token-here"
curl -X GET http://localhost:5000/api/modules \
  -H "Authorization: Bearer $TOKEN"

# View frontend
# Open: http://localhost:3000
```

## Debugging

### View Logs

```bash
# All services
make logs

# Specific service
make logs-api          # Backend
make logs-db           # PostgreSQL
make logs-frontend     # Frontend
make logs-worker       # Worker
```

### Shell Access

```bash
# PostgreSQL shell
make shell-db

# Backend container bash
make shell-api

# Frontend container shell
make shell-frontend
```

### Database Inspection

```bash
# Connect to database
make shell-db

# Common queries
\dt                      # List tables
\d+ deployments          # Describe table
SELECT * FROM users;     # View users
SELECT * FROM modules;   # View modules
```

### Backend Debugging

1. Add breakpoints in VS Code
2. If using Dev Container: F5 launches debugger automatically
3. Or attach debugger to running container via "Debug > Attach to Process"

### Performance Issues

```bash
# Check container resource usage
docker stats azselfservice-backend

# View slow queries (PostgreSQL)
# Enable slow query log in devcontainer.json and restart

# Check network latency
docker-compose exec frontend curl -w "@-" http://backend:5000
```

## Testing

### API Integration Tests

```bash
# Run in backend container
make shell-api
cd AzSelfService.API
dotnet test

# Or via docker-compose
docker-compose exec backend dotnet test
```

### Frontend Component Tests

```bash
# Run in frontend container
make shell-frontend
npm test

# Watch mode
npm test -- --watch
```

### End-to-End Tests

1. Start environment: `make up`
2. Run E2E test suite (to be implemented in Phase 5)

## Common Tasks

### Resetting to Fresh State

```bash
# Wipe everything and start fresh
make clean
make setup
make up
```

### Inspecting Database State

```bash
# Connect to PostgreSQL
make shell-db

# View audit logs
SELECT * FROM audit_logs ORDER BY timestamp DESC LIMIT 10;

# View recent deployments
SELECT id, status, created_at FROM deployments ORDER BY created_at DESC;

# Check module registrations
SELECT name, version, is_published FROM modules;
```

### Testing Terraform Module

```bash
# Deploy a Resource Group via API
curl -X POST http://localhost:5000/api/deployments \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -d '{
    "module_id": "...",
    "inputs": {
      "name": "test-rg",
      "location": "eastus"
    }
  }'

# Poll logs
curl -X GET "http://localhost:5000/api/deployments/{id}/logs" \
  -H "Authorization: Bearer $TOKEN"
```

### Running Worker Manually

```bash
# For testing job processing locally
make shell-api
cd ../AzSelfService.Worker
dotnet run

# This will poll the job queue and process deployments
```

### Environment Variable Changes

1. Edit `.env`
2. Restart services: `make restart`
3. Or edit individual service env in `docker-compose.yml`

## Troubleshooting

### "PostgreSQL is not ready"

```bash
# Check PostgreSQL status
docker-compose logs postgres

# Restart PostgreSQL
docker-compose restart postgres

# Wait and retry
make up
```

### "Backend cannot connect to database"

```bash
# Verify connection string in .env
# Test from backend container
make shell-api
psql postgresql://postgres:password@postgres:5432/azselfservice
```

### "Port already in use"

```bash
# Find process using port 5000
lsof -i :5000

# Or stop competing containers
docker ps
docker stop <container-id>

# Then retry
make up
```

### "Frontend cannot reach backend"

1. Check backend is healthy: `curl http://localhost:5000/health`
2. Check network: `docker network ls` and `docker network inspect azselfservice`
3. Frontend env variable: `NEXT_PUBLIC_API_BASE_URL=http://localhost:5000`

### "Cannot write to logs directory"

```bash
# Fix permissions
mkdir -p logs
chmod 777 logs

# Or recreate
rm -rf logs
mkdir -p logs
```

### "Docker image build fails"

```bash
# Rebuild from scratch
docker-compose down
docker-compose build --no-cache backend

# Then start
make up
```

### Database migrations not applying

```bash
# Check Entity Framework is running
make shell-api
dotnet ef database update

# Or force in code:
# Set ASPNETCORE_ENVIRONMENT=Development in devcontainer.json
# EF will auto-migrate on startup
```

## Getting Help

- **Architecture**: See `docs/architecture/solution-overview.md`
- **Auth Flow**: See `docs/architecture/auth-model.md`
- **Database Schema**: See `docs/architecture/database-design.md`
- **Terraform Execution**: See `docs/architecture/terraform-execution.md`
- **API Docs**: http://localhost:5000/swagger (when running)
- **Logs**: `make logs` shows real-time output

## Next Steps (After Phase 2)

- **Phase 3**: Terraform execution core implementation
- **Phase 4**: Module registry HTTP API
- **Phase 5**: Frontend UI with form generation
- **Phase 6**: Audit logging and compliance features
- **Phase 7**: Production hardening (HTTPS, RBAC, B2C auth)
- **Phase 8**: Infrastructure as Code (Bicep/Terraform for production deployment)
