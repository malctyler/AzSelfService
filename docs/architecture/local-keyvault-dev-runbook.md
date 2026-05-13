# Local Key Vault Development Runbook

This runbook documents the repeatable local workflow for using Azure Key Vault credentials in Docker-based development.

## Goal

- Keep secrets out of source control.
- Load service principal credentials from CLIXML into local `.env` (git-ignored).
- Start backend/worker with those values.
- Verify credential preflight returns `PASS`.

## Prerequisites

- Docker Desktop is running.
- `docker-compose` is available.
- A CLIXML credential file exists locally (for example `C:\Users\<you>\CredentialStore\PROD-Automation.clixml`).
- Key Vault and customer metadata are already configured.

## One-Time Local Setup

If `.env` is missing, generate it first:

```powershell
Set-Location D:\PlayGround\AzSelfService
./scripts/dev-setup.sh
```

## Load CLIXML Credentials Into `.env`

Run this in PowerShell to update local `.env` safely (file is ignored by git):

```powershell
Set-Location D:\PlayGround\AzSelfService

$clixmlPath = 'C:\Users\malcolm.COTTAGES\CredentialStore\PROD-Automation.clixml'
$creds = Import-Clixml -LiteralPath $clixmlPath
$networkCredential = $creds.GetNetworkCredential()

$clientId = $networkCredential.UserName
$clientSecret = $networkCredential.Password
$tenantId = 'bf0465f4-f8c0-4ff4-978d-af5315afa795'
$keyVaultName = 'azselfservice-1338-kv'
$keyVaultUrl = 'https://azselfservice-1338-kv.vault.azure.net/'

$envText = Get-Content .env -Raw
$envText = [regex]::Replace($envText, '(?m)^AZURE_CLIENT_ID=.*$', "AZURE_CLIENT_ID=$clientId")
$envText = [regex]::Replace($envText, '(?m)^AZURE_CLIENT_SECRET=.*$', "AZURE_CLIENT_SECRET=$clientSecret")
$envText = [regex]::Replace($envText, '(?m)^AZURE_TENANT_ID=.*$', "AZURE_TENANT_ID=$tenantId")
$envText = [regex]::Replace($envText, '(?m)^AZURE_KEYVAULT_NAME=.*$', "AZURE_KEYVAULT_NAME=$keyVaultName")
$envText = [regex]::Replace($envText, '(?m)^AZURE_KEYVAULT_URL=.*$', "AZURE_KEYVAULT_URL=$keyVaultUrl")
Set-Content .env $envText -NoNewline
```

## Start Services With Updated Env

```powershell
Set-Location D:\PlayGround\AzSelfService
docker-compose --profile dev up -d --force-recreate backend worker
```

## Verify Credential Preflight

```powershell
Set-Location D:\PlayGround\AzSelfService

$customerId = 'f42fc931-b1ba-49e0-8a8b-5aea0217ab0e'
$login = Invoke-RestMethod -Method Post -Uri 'http://localhost:5000/api/auth/login' -ContentType 'application/json' -Body '{"username":"admin","password":"Test@1234"}'
curl.exe -s -H "Authorization: Bearer $($login.token)" "http://localhost:5000/api/customers/$customerId/credential-preflight"
```

Expected result:

- `status`: `PASS`
- `canProceed`: `true`

## Quick Troubleshooting

- `status=FAIL` with `DefaultAzureCredential` messages:
  - Recheck `.env` values for `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_CLIENT_SECRET`.
  - Recreate backend/worker containers after any `.env` change.
- `Name or service not known` for Key Vault host:
  - Verify `AZURE_KEYVAULT_URL` points to the active vault.
- `403` from Key Vault:
  - Confirm service principal has Key Vault RBAC role on the vault scope.

## Security Notes

- Never commit `.env`.
- Never paste live secrets into docs, PR comments, or chat logs.
- Rotate temporary service principal secrets after troubleshooting sessions when required.
