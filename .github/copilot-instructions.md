# AzSelfService Repository Instructions

This is the canonical repository instruction file for GitHub Copilot. Keep it updated as the project evolves.

## Current Truths

- PostgreSQL is the primary datastore for users, customers, deployments, logs, outputs, and module/package metadata.
- Deployment execution is queue-based and asynchronous. The API creates deployment records; the worker processes them later.
- The worker is the place where Terraform is executed. The API should not run Terraform directly during a normal deployment flow.
- Local username/password auth is the MVP auth model. The Entra ID B2C path remains a post-MVP migration plan.
- Customer isolation is enforced in queries and data access paths. Do not introduce cross-customer reads without an explicit admin design.
- Deployment error text should not remain stale when a deployment is back in RUNNING. The worker clears stale error state when work resumes.
- Deployment details in the portal are driven by persisted state and log records; do not describe the UX as live push streaming unless that is actually implemented.
- Windows marketplace post-install now uses a generated script and signed read URIs for package payloads when available.
- Software packages are validated before publish/upload, but validation is still structural plus metadata-based. It does not prove that an installer family will silently install in every environment.

## Package Policy

- Package archives must keep the required zip shape: `manifest.json`, `checksums.sha256`, `scripts/install.ps1`, `scripts/detect.ps1`, and the installer payload.
- `manifest.json` must include `installCommand` and `silentArgs`.
- `silentArgs` must be package-specific and non-empty.
- `manifest.json` must include `silentInstallArgsTested = true` and `rebootSuppressionArgsTested = true` to declare unattended and no-reboot argument verification.
- MSI packages must use `installCommand = msiexec.exe`.
- Update the package convention doc and validator together when the manifest contract changes.

## Architecture References

- [docs/architecture/solution-overview.md](../docs/architecture/solution-overview.md)
- [docs/architecture/auth-model.md](../docs/architecture/auth-model.md)
- [docs/architecture/database-design.md](../docs/architecture/database-design.md)
- [docs/architecture/module-framework.md](../docs/architecture/module-framework.md)
- [docs/architecture/terraform-execution.md](../docs/architecture/terraform-execution.md)
- [docs/architecture/software-package-convention.md](../docs/architecture/software-package-convention.md)
- [docs/architecture/local-keyvault-dev-runbook.md](../docs/architecture/local-keyvault-dev-runbook.md)
- [docs/adr/0001-postgres-over-cosmosdb.md](../docs/adr/0001-postgres-over-cosmosdb.md)
- [docs/adr/0002-job-queue-pattern.md](../docs/adr/0002-job-queue-pattern.md)
- [docs/adr/0003-local-auth-mvp.md](../docs/adr/0003-local-auth-mvp.md)

## Known Drift To Watch

- Some older docs still say "real-time log streaming." The implementation currently persists logs and deployment state and the UI polls for updates.
- The package convention doc should stay aligned with `SoftwarePackageValidationService` and `scripts/new-software-package.ps1`.
- When API or worker code changes, rebuild the dev containers so the running binaries stay current.

## Maintenance Rule

- If a future change alters any of the items above, update this file in the same change.
- If a new durable fact about the codebase, workflow, or packaging policy is discovered, add it here before closing the task.
- If a doc conflicts with code, treat the code as current reality and update the doc.