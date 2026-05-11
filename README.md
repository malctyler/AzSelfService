# AzSelfService Platform

**Controlled self-service Azure provisioning platform with Terraform orchestration.**

A web-based platform that enables customers to deploy approved infrastructure modules (Resource Groups, Storage Accounts, etc.) without requiring direct Terraform expertise.

---

## 🎯 What Is This?

AzSelfService abstracts Terraform complexity behind a clean web UI:

1. **Customer** submits deployment request via form (e.g., "Create Resource Group in East US")
2. **Platform** validates inputs, queues job, executes Terraform asynchronously
3. **Worker** runs `terraform apply`, streams logs in real-time
4. **Customer** sees deployment status, outputs, and complete audit trail

**Core principle:** Terraform is the source of truth; the platform is the orchestration layer.

---

## 🚀 Quick Start

### Prerequisites

- Docker & Docker Compose
- VS Code (optional; recommended)
- Git

### Local Development Setup

```bash
# Clone the repository
git clone https://github.com/your-org/azselfservice.git
cd azselfservice

# Option 1: Open in VS Code with DevContainer
code .
# Wait for DevContainer to open (Extensions → Dev Containers → Reopen in Container)

# Option 2: Manual setup
docker-compose up

# Initialize database
./scripts/dev-setup.sh

# Access the platform
# Frontend: http://localhost:3000
# Backend API: http://localhost:5000/swagger
# Login: admin / Test@1234 (see docs/architecture/auth-model.md)
```

---

## 📚 Architecture & Design

Start here to understand the platform:

| Document | Purpose |
|----------|---------|
| [Solution Overview](docs/architecture/solution-overview.md) | Platform vision, customer flows, MVP scope |
| [Authentication Model](docs/architecture/auth-model.md) | User auth, authorization, multi-tenancy |
| [Terraform Execution](docs/architecture/terraform-execution.md) | Job queue, state management, log streaming |
| [Database Design](docs/architecture/database-design.md) | Data model, schema, migrations |
| [Module Framework](docs/architecture/module-framework.md) | How modules become products, versioning |

### Architecture Decision Records (ADRs)

| Document | Decision |
|----------|----------|
| [ADR 0001](docs/adr/0001-postgres-over-cosmosdb.md) | Why PostgreSQL (not Cosmos DB) |
| [ADR 0002](docs/adr/0002-job-queue-pattern.md) | Why job queue (not direct execution) |
| [ADR 0003](docs/adr/0003-local-auth-mvp.md) | Why local auth for MVP (migrate to B2C post-MVP) |

---

## 🏗️ Project Structure

```
azselfservice/
├── .devcontainer/              # VS Code Dev Container config
│   └── devcontainer.json
├── backend/                    # ASP.NET Core API & Worker
│   ├── AzSelfService.API/
│   ├── AzSelfService.Core/
│   ├── AzSelfService.Infrastructure/
│   ├── AzSelfService.Worker/
│   └── tests/
├── frontend/                   # React + Next.js + TypeScript
│   ├── src/
│   ├── public/
│   └── package.json
├── terraform-modules/          # Terraform module definitions
│   ├── resource-group/
│   │   ├── main.tf
│   │   ├── variables.tf
│   │   ├── outputs.tf
│   │   └── module.yaml
│   └── README.md
├── infrastructure/             # Platform infrastructure (Bicep/Terraform)
│   └── README.md
├── scripts/                    # Development scripts
│   ├── dev-setup.sh
│   ├── dev-up.sh
│   └── dev-down.sh
├── docs/                       # Architecture docs & ADRs
│   ├── architecture/
│   └── adr/
├── docker-compose.yml          # Local dev environment
├── .env.example               # Environment template
└── README.md                  # This file
```

---

## 💻 Tech Stack

| Layer | Technology |
|-------|-----------|
| **Frontend** | React + Next.js + TypeScript |
| **Backend** | ASP.NET Core (.NET 9) |
| **Database** | PostgreSQL (Azure Database for PostgreSQL Flexible Server) |
| **Auth** | Local (MVP) → B2C (post-MVP) |
| **Terraform Execution** | Job queue + dedicated worker |
| **State Storage** | Azure Blob Storage |
| **Hosting** | Azure Static Web Apps (frontend), App Service (backend) |

---

## 📋 Implementation Phases

| Phase | Focus | Timeline |
|-------|-------|----------|
| **Phase 1** | Architecture & Docs | 2-3 days |
| **Phase 2** | Dev Environment & Scaffolding | 2-3 days |
| **Phase 3** | Terraform Execution Core | 3-4 days |
| **Phase 4** | Module Registry & Validation | 1-2 days |
| **Phase 5** | Frontend UI | 3-4 days |
| **Phase 6** | Audit & History | 1-2 days |
| **MVP Total** | | 13-18 days |
| **Phase 7** | Security Hardening | 2-3 days |
| **Phase 8** | Network & Production Deployment | 2-3 days |

See [implementation plan](docs/PLAN.md) for detailed phase breakdown.

---

## ✨ MVP Features

### ✓ Included

- [x] Local username/password authentication
- [x] Multi-tenant user & customer model
- [x] Resource Group Terraform module (deployable MVP)
- [x] Deployment request form with validation
- [x] Async job queue with worker execution
- [x] Real-time log streaming
- [x] Terraform output display
- [x] Full audit trail
- [x] Complete API documentation (OpenAPI/Swagger)

### ⏸️ Post-MVP (Planned, Not Built)

- [ ] Entra ID B2C authentication
- [ ] Multiple Terraform modules (Storage, Key Vault, VNet, etc.)
- [ ] Deployment approvals workflow
- [ ] Terraform plan visualization
- [ ] Drift detection
- [ ] Rollback capability
- [ ] RBAC inheritance
- [ ] Dependency orchestration

---

## 🔐 Security Considerations

### MVP Implementation

- Passwords hashed with bcrypt
- JWT token-based sessions (24-hour expiration)
- Customer data isolation enforced at DB query level
- Service Principal credentials stored in Azure Key Vault (never in DB)
- All API requests logged and auditable

### Pre-Production (Phase 7)

- Input sanitization & XSS prevention
- Rate limiting on APIs
- HTTPS enforcement
- CORS policy hardening
- Terraform plan validation before apply
- Managed identity RBAC for worker

---

## 🚢 Deployment

### Local Development

```bash
docker-compose up
```

### Production (Post-MVP)

```bash
# Infrastructure deployment (Phase 8)
bicep build infrastructure/main.bicep
az deployment sub create --template-file main.json

# Application deployment
azd up
```

See [deployment guide](infrastructure/README.md) for full instructions.

---

## 📖 Usage Example

### Create a Resource Group

**Via UI:**

1. Log in: `admin` / `Test@1234`
2. Dashboard → "New Deployment"
3. Select: "Resource Group" module
4. Fill form: Name = "my-rg", Location = "eastus"
5. Click: "Deploy"
6. Watch logs stream in real-time
7. View outputs and audit trail

**Via API:**

```bash
# Login
curl -X POST http://localhost:5000/api/auth/login \
  -H "Content-Type: application/json" \
  -d '{"username":"admin","password":"Test@1234"}'

# Get deployment modules
curl -X GET http://localhost:5000/api/modules \
  -H "Authorization: Bearer <token>"

# Submit deployment
curl -X POST http://localhost:5000/api/deployments \
  -H "Authorization: Bearer <token>" \
  -H "Content-Type: application/json" \
  -d '{
    "module_id": "...",
    "inputs": {
      "resource_group_name": "my-rg",
      "location": "eastus"
    }
  }'

# Poll status
curl -X GET http://localhost:5000/api/deployments/<deployment_id> \
  -H "Authorization: Bearer <token>"
```

---

## 🤝 Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md) (post-MVP).

---

## 📞 Support & Issues

- **Architecture Questions:** See [docs/architecture/](docs/architecture/)
- **API Documentation:** `http://localhost:5000/swagger`
- **Issues:** GitHub Issues
- **Discussions:** GitHub Discussions

---

## 📄 License

[Your License Here]

---

## 🙏 Acknowledgments

- Terraform Documentation & Community
- Azure Best Practices
- .NET Core Team
- React & Next.js Communities

---

## 🎓 Learning Resources

- [Terraform Documentation](https://www.terraform.io/docs/)
- [Azure SDK for .NET](https://github.com/Azure/azure-sdk-for-net)
- [React Best Practices](https://react.dev/)
- [ASP.NET Core Documentation](https://learn.microsoft.com/en-us/aspnet/core/)

---

**Last Updated:** May 11, 2026  
**Maintainer:** [Your Name/Team]

