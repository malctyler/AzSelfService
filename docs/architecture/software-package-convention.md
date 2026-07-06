# Software Package Convention

This document defines the upload and packaging convention for customer-installable software archives.

## Goals

- Standardize package names and archive contents.
- Allow automated validation before publish.
- Make VM installation deterministic and auditable.

## Naming Convention

Package zip filename format:

`vendor-product-version-os-arch-installer.zip`

Example:

`igorpavlov-7zip-24.09.0-windows-x64-msi.zip`

Rules:

- Lowercase letters, digits, and hyphens only.
- `version` must be semver (`major.minor.patch`).
- Do not overwrite an existing package version.

## Required Zip Structure

Each package zip must contain:

- `manifest.json`
- `checksums.sha256`
- `payload/<installer file>`
- `scripts/install.ps1`
- `scripts/detect.ps1`

## Manifest Contract

Minimum required fields:

- `packageId`
- `displayName`
- `version`
- `publisher`
- `os`
- `architecture`
- `installerType`
- `entrypoint`
- `installCommand`
- `silentArgs`
- `silentInstallArgsTested`
- `rebootSuppressionArgsTested`
- `expectedExitCodes`
- `rebootBehavior`
- `detectionRules`
- `artifacts[]` (with SHA256)

Notes:

- `silentArgs` must be package-specific and non-empty.
- `installCommand` must be non-empty and match the installer type; for MSI packages, use `msiexec.exe`.
- `silentInstallArgsTested` must be `true`.
- `rebootSuppressionArgsTested` must be `true`.
- Validate the zip with `POST /api/admin/software-packages/validate` before publish/upload.

## Storage Layout

Use container `packages` in the software storage account.

Blob paths:

- Platform catalog: `catalog/platform/{packageId}/{version}/{zipName}`
- Customer catalog: `catalog/customers/{customerId}/{packageId}/{version}/{zipName}`

## Tooling in This Repo

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
  -DisplayName '7-Zip' `
  -SilentInstallArgsTested `
  -RebootSuppressionArgsTested
```

Publish a package zip to Azure Blob Storage:

```powershell
.\scripts\publish-software-package.ps1 `
  -ZipPath '.\software-packages\igorpavlov-7zip-24.09.0-windows-x64-msi.zip' `
  -PackageId 'igorpavlov.7zip' `
  -Version '24.09.0' `
  -StorageAccountName 'azselfservicesoftware01' `
  -ContainerName 'packages' `
  -Scope 'platform'
```

## Recommended Next Step

Add backend package validation endpoint that rejects packages unless:

- Filename convention matches.
- Required files exist inside zip.
- `manifest.json` conforms to schema.
- Artifact checksums match package contents.

## API Workflow

The API now supports admin validation and publish/catalog operations.

0. Optional one-step upload + validate + catalog publish:

```http
POST /api/admin/software-packages/upload
Content-Type: multipart/form-data
Authorization: Bearer <admin-token>

scope=platform
storageAccountName=azselfservicesoftware01
containerName=packages
isPublished=true
PackageFile=@igorpavlov-7zip-24.09.0-windows-x64-msi.zip
```

1. Validate zip before publish:

```http
POST /api/admin/software-packages/validate
Content-Type: multipart/form-data
Authorization: Bearer <admin-token>

PackageFile=@igorpavlov-7zip-24.09.0-windows-x64-msi.zip
```

2. Publish metadata to catalog (platform scope example):

```http
POST /api/admin/software-packages/publish
Content-Type: application/json
Authorization: Bearer <admin-token>

{
  "scope": "platform",
  "packageId": "igorpavlov.7zip",
  "version": "24.09.0",
  "displayName": "7-Zip",
  "publisher": "Igor Pavlov",
  "os": "windows",
  "architecture": "x64",
  "installerType": "msi",
  "blobPath": "catalog/platform/igorpavlov.7zip/24.09.0/igorpavlov-7zip-24.09.0-windows-x64-msi.zip",
  "zipSha256": "4e8a9b0b802280e24da41eac9e06c303321b8af4f693e1ddc6b0814cac81decd",
  "isPublished": true
}
```

3. Query catalog:

```http
GET /api/admin/software-packages?scope=platform
Authorization: Bearer <admin-token>
```

## Silent Install Consistency

For future catalog packages, the manifest must describe the exact silent install behavior the VM will run.

- Use `silentArgs` to encode the package-specific unattended flags.
- Set `silentInstallArgsTested = true` only after manually confirming those args perform an unattended install.
- Set `rebootSuppressionArgsTested = true` only after confirming the args suppress auto-reboot so platform sequencing controls reboot timing.
- Do not rely on a generic default for all EXE installers.
- If the installer family has special silent switches, record them explicitly in the manifest and verify before publish.
