# ADR 0001: PostgreSQL Over Cosmos DB for Primary Datastore

**Date:** May 11, 2026  
**Status:** ACCEPTED  
**Context:** Selecting primary database for AzSelfService platform  

## Problem

We need a primary datastore for:
- User and customer records
- Deployment job queue
- Terraform execution logs and outputs
- Audit trail
- Module registry

Two leading candidates:
1. **Cosmos DB (Azure native, NoSQL, document-based)**
2. **PostgreSQL (relational, ACID, open-source)**

## Decision

**We choose PostgreSQL (via Azure Database for PostgreSQL Flexible Server).**

## Rationale

### 1. Relational Data Model (Critical)

**AzSelfService has highly relational data:**

- Customers own Users
- Customers own Deployments
- Modules have many Deployments
- Deployments have Inputs, Outputs, Logs, and Audit records
- Users appear in Audit logs
- Strong referential integrity needed (e.g., customer cannot be deleted if deployments exist)

**Cosmos strength:** Flexible, JSON-friendly  
**Cosmos weakness:** Lacks native relationships; audit chains require manual joins; complex queries are awkward

**PostgreSQL strength:** ACID transactions, foreign keys, complex queries, strong integrity  
**PostgreSQL weakness:** Less flexible schema (but not a problem for us; schema is stable)

### 2. JSONB Support (Best of Both Worlds)

**Cosmos:** Pure document database; everything is JSON  
**PostgreSQL:** Relational core + JSONB columns for flexibility

We need both:
- **Relational:** deployments.customer_id references customers.id (referential integrity)
- **Flexible:** deployment_inputs is JSONB (customer form submissions vary by module)

**Result:** PostgreSQL gives us relational integrity where it matters + JSON flexibility where we need it.

### 3. Audit Trail Requirements

Audit logs are central to AzSelfService (compliance requirement).

**Cosmos drawback:** Document database makes audit chains hard; querying "all deployments by user X across time" requires complex queries  
**PostgreSQL strength:** `SELECT * FROM audit_logs WHERE actor_id = X ORDER BY timestamp DESC` — simple, fast, familiar

### 4. Cost & Ecosystem

| Factor | Cosmos | PostgreSQL |
|--------|--------|------------|
| Cost (MVP scale) | ~$30/month (provisioned) | ~$15/month (flexible tier) |
| Scaling | Pay for RU throughput | Pay for compute + storage |
| Cost predictability | Complex (burst pricing) | Simple (per-compute tier) |
| Backup | Built-in, expensive | Built-in, cheap |
| Local dev | Not easily emulated | Docker container |
| Terraform support | Good | Better (mature ecosystem) |

### 5. Development Productivity

**MVP timeline:** 2-3 weeks to core platform  
**PostgreSQL advantage:** Developers already understand relational DB; migration tools (Entity Framework, Alembic) mature; debugging easier

### 6. Migration Path

**Future scenario:** If we need NoSQL (multi-cloud), we can:
- Keep PostgreSQL for core (user, audit, module registry)
- Add Cosmos later for specific high-volume workloads (e.g., sensor time-series)
- Hybrid approach is common

---

## Consequences

### Positive

✓ Clear schema and relationships  
✓ Strong data integrity  
✓ Audit trails are trivial to query  
✓ Cheaper for MVP scale  
✓ Better Terraform ecosystem  
✓ Easier local development  
✓ Faster queries (indexes work as expected)  

### Negative

✗ Schema changes require migrations (manageable with Entity Framework)  
✗ Scaling beyond single-node requires careful planning (not an MVP concern)  
✗ Not as "cloud-native" feeling as Cosmos (but objectively better for our use case)  

---

## Alternatives Considered

### Alternative 1: Cosmos DB

**Why considered:** Azure-native, serverless billing  
**Why rejected:** Relational data model doesn't fit; audit queries would be complex; cost higher for workload

### Alternative 2: SQL Server

**Why considered:** Robust, enterprise-grade  
**Why rejected:** More expensive; ecosystem slightly less friendly to Terraform; harder to run locally

---

## Implementation Notes

- Use **Azure Database for PostgreSQL Flexible Server** (cheaper than single-server, more flexible)
- Connect via Entity Framework Core (C#)
- Run locally via `docker run -e POSTGRES_PASSWORD=... postgres:15`
- Migrations via `dotnet ef migrations add` and `dotnet ef database update`

---

## References

- [Cosmos DB Pricing](https://azure.microsoft.com/en-us/pricing/details/cosmos-db/)
- [PostgreSQL Pricing](https://azure.microsoft.com/en-us/pricing/details/postgresql/flexible-server/)
- [Entity Framework Core](https://learn.microsoft.com/en-us/ef/core/)
- [PostgreSQL vs Cosmos Comparison](https://learn.microsoft.com/en-us/azure/cosmos-db/sql/compare-relational-databases)

