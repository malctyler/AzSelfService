# AzSelfService Platform — Authentication Model

## Overview

This document defines how users authenticate to the platform, how their identity is managed, how they authorize against customer resources, and the migration path to enterprise authentication.

---

## MVP Authentication Strategy (Local Provider)

### Simple & Self-Contained

For MVP, we use a **local username/password** authentication model:

- **No external dependencies:** No Azure AD, no OIDC provider, no third-party auth service
- **Easy to test locally:** Works with docker-compose, no configuration required
- **Fast iteration:** Changing auth logic doesn't require external service changes
- **Clear migration path:** Post-MVP, upgrade to Azure Entra ID B2C without breaking existing auth

### Authentication Flow

```
1. User navigates to login page
   ↓
2. Enter username & password
   ↓
3. API: POST /api/auth/login { username, password }
   ↓
4. Backend: 
   - Query users table: SELECT * FROM users WHERE username = ?
   - Verify password_hash using bcrypt.verify()
   - If valid:
     - Generate JWT token { user_id, customer_id, username, iat, exp }
     - Return token to frontend
   - If invalid: Return 401 Unauthorized
   ↓
5. Frontend: Store JWT in localStorage
   ↓
6. Frontend: Attach JWT to all subsequent API requests (Authorization: Bearer <token>)
   ↓
7. Backend: Validate JWT on every request
   - Extract claims from token
   - Verify signature using secret key
   - If valid: Proceed with request (scoped to customer_id in token)
   - If invalid/expired: Return 401, force re-login
```

### JWT Token Structure

```json
{
  "user_id": "f47ac10b-58cc-4372-a567-0e02b2c3d479",
  "customer_id": "a1b2c3d4-e5f6-47a8-9b0c-1d2e3f4a5b6c",
  "username": "devops@contoso.com",
  "email": "devops@contoso.com",
  "iat": 1715430130,
  "exp": 1715516530
}
```

**Claims:**
- `user_id` — User UUID (primary key from users table)
- `customer_id` — Customer UUID (primary key from customers table, foreign key in users)
- `username` — User's login name (for logging/audit)
- `email` — User's email (for notifications, future)
- `iat` — Issued at (Unix timestamp)
- `exp` — Expires at (Unix timestamp); typically 24 hours from issue

**Token Configuration:**
- **Expiration:** 24 hours (MVP); configurable via appsettings.json
- **Secret Key:** Stored in appsettings.json (dev) → Azure Key Vault (production)
- **Algorithm:** HS256 (HMAC-SHA256)

### Password Security

**Hashing:**
- Use **bcrypt** (industry standard for password hashing)
- Cost factor: 10 (default; ~100ms hash time)
- Never store plaintext passwords

**API Implementation:**
```csharp
// At registration (admin creates user):
var passwordHash = BCrypt.Net.BCrypt.HashPassword(plainPassword, workFactor: 10);
user.PasswordHash = passwordHash;
await db.SaveChangesAsync();

// At login:
var user = await db.Users.FirstOrDefaultAsync(u => u.Username == username);
if (BCrypt.Net.BCrypt.Verify(password, user.PasswordHash)) {
    // Login successful
}
```

---

## User & Customer Model

### Data Model

#### Customers Table

```sql
CREATE TABLE customers (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  name VARCHAR(255) NOT NULL,
  subscription_id VARCHAR(255) NOT NULL,
  tenant_id VARCHAR(255) NOT NULL,
  created_at TIMESTAMP DEFAULT NOW(),
  updated_at TIMESTAMP DEFAULT NOW()
);
```

**Semantics:**
- `subscription_id` — Azure subscription where customer resources are deployed
- `tenant_id` — Azure tenant (Entra ID directory) where customer's identities live
- One customer = one Azure subscription (MVP; could be N-to-N post-MVP)

#### Users Table

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
```

**Semantics:**
- Multi-tenant: many users per customer
- `customer_id` — Foreign key enforcing customer isolation
- `is_active` — Soft delete (set to false to deactivate)
- `username` — Unique globally (no two users across all customers have same username)

### Authorization Rules

#### Core Principle: Customer Isolation

**Every API request must be scoped to the requesting user's customer.**

#### Rule 1: Users Can Only View/Modify Own Customer's Data

```csharp
// Backend filter pattern:
var customerId = User.FindFirst("customer_id").Value; // from JWT
var deployments = await db.Deployments
    .Where(d => d.CustomerId == customerId)
    .ToListAsync();
```

**Implication:**
- Customer A's user cannot see Customer B's deployments
- Database queries ALWAYS filter by customer_id
- No "admin bypass" view of other customers (can add role-based access later)

#### Rule 2: Service Principal Secrets Are Never Visible

- Stored only in Azure Key Vault
- Never returned in API responses
- Worker retrieves at runtime using Managed Identity

#### Rule 3: Audit Logs Cannot Be Tampered

- All deployment actions logged with immutable timestamp
- Audit logs queryable only for user's own customer

---

## Service Principal Management (Terraform Execution)

### What Is the Service Principal?

The **Service Principal (SP)** is the Azure identity that Terraform uses to authenticate and create resources in the customer's subscription.

**Example SP credentials:**
```
Client ID: 12345678-1234-1234-1234-123456789012
Client Secret: my-secret-value-abc123xyz
Tenant ID: 87654321-4321-4321-4321-210987654321
Subscription ID: xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx
```

### Credential Storage Strategy

#### Never Store in Database

✗ Database is too visible; breach exposes all credentials

#### Store in Azure Key Vault

✓ Dedicated secret storage; access controlled via RBAC

**Key Vault structure:**
```
Key Vault: <environment-specific-vault>

Secrets:
  customers/{customer_id}/sp-client-secret
```

The secret value stores the client secret only. The service principal app id is stored in secret metadata:

```
Key: customers/a1b2c3d4-e5f6-47a8-9b0c-1d2e3f4a5b6c/sp-client-secret
Value: my-secret-value-abc123xyz
Content-Type: appid=12345678-1234-1234-1234-123456789012
```

Tenant and subscription are stored in the customer row:

```sql
customers.tenant_id
customers.subscription_id
```

### Credential Injection at Deployment Time

When the worker executes Terraform:

1. **Worker starts** with Managed Identity (platform's identity)
2. **Worker reads from Key Vault** using Managed Identity:
   ```csharp
   var kvUri = "https://{vault-name}.vault.azure.net/";
   var client = new SecretClient(new Uri(kvUri), new DefaultAzureCredential());
   
   var clientSecret = await client.GetSecretAsync($"customers/{customerId}/sp-client-secret");
   var clientId = ParseAppIdFromMetadata(clientSecret.Value.Properties.ContentType);
   var tenantId = customer.TenantId;
   var subscriptionId = customer.SubscriptionId;
   ```

3. **Worker sets environment variables** for Terraform:
   ```bash
   export ARM_CLIENT_ID="12345678-1234-1234-1234-123456789012"
   export ARM_CLIENT_SECRET="my-secret-value-abc123xyz"
   export ARM_TENANT_ID="87654321-4321-4321-4321-210987654321"
   export ARM_SUBSCRIPTION_ID="xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx"
   ```

4. **Terraform uses env vars** automatically:
   ```hcl
   provider "azurerm" {
     features {}
     # No credentials hardcoded; uses ARM_* environment variables
   }
   ```

5. **Terraform executes** in customer's subscription with SP credentials
6. **Worker logs and outputs** are persisted; credentials are ephemeral (not logged)

### Credential Rotation Strategy

**MVP:** Manual rotation
- Platform admin: rotate SP in Azure portal
- Platform admin: update secret in Key Vault
- Next deployment: uses new credentials

**Post-MVP:** Automated rotation
- Scheduled job: rotate SP client secret weekly
- Update Key Vault automatically
- Seamless, no manual intervention

---

## Post-MVP: Migration to Entra ID B2C

### Why B2C?

**B2C benefits:**
- Enterprise SSO (Contoso users log in with corporate accounts)
- Multi-factor authentication (MFA)
- Self-service password reset
- Audit trail of logins
- SAML/OIDC federation

### Migration Path (Non-Breaking)

**Phase 1: Add B2C Alongside Local Auth**
- Deploy B2C tenant
- Create app registration in B2C
- Add MSAL SDK to frontend
- Add B2C auth endpoint to backend
- Users can choose: "Login with Local Account" or "Login with B2C"

**Phase 2: Migrate Users**
- Customers migrate their users from local to B2C
- Local auth still works as fallback

**Phase 3: Deprecate Local Auth**
- Disable local auth endpoint
- All users must use B2C

**Zero downtime, customer-driven migration.**

### B2C Configuration Sketch

```json
{
  "AzureAdB2C": {
    "Instance": "https://azselfservice.b2clogin.com",
    "ClientId": "xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx",
    "CallbackPath": "/signin-oidc",
    "Domain": "azselfservice.onmicrosoft.com",
    "SignUpSignInPolicyId": "B2C_1_susi",
    "ResetPasswordPolicyId": "B2C_1_reset"
  }
}
```

---

## Logout & Session Management

### Logout

```
1. User clicks "Logout" button
   ↓
2. Frontend: DELETE /api/auth/logout
   ↓
3. Backend: Blacklist token (optional; add to cache if needed)
   ↓
4. Frontend: Clear JWT from localStorage, redirect to login
```

**MVP:** No server-side token blacklist (JWT is stateless)
- Token remains valid until exp time
- For security-sensitive scenarios (post-MVP): maintain blacklist in Redis cache

### Session Timeout

- **Token expiration:** 24 hours
- **Inactivity timeout:** Not in MVP (all tokens valid until exp)
- **Post-MVP:** Implement inactivity tracking; force re-auth after 1 hour idle

---

## Future: Role-Based Access Control

### Post-MVP Extension

Add roles to user model:

```sql
ALTER TABLE users ADD COLUMN role VARCHAR(255) DEFAULT 'deployer';

-- Enum: admin, deployer, viewer
```

**Roles:**
- **admin** — Create customers, register modules, view all deployments, manage users
- **deployer** — Submit deployments, view own deployments, retry failed deployments
- **viewer** — View deployments, view audit logs, no execute permissions

**Authorization:** Check role claim in JWT before allowing action.

---

## Security Best Practices (MVP)

### Do's

✓ Hash passwords with bcrypt  
✓ Use HTTPS only (enforce in production)  
✓ Store JWT secret in Key Vault (production)  
✓ Validate JWT on every request  
✓ Filter all queries by customer_id  
✓ Log authentication events  

### Don'ts

✗ Never log passwords or tokens  
✗ Never expose SP credentials in API responses  
✗ Never trust client-side authorization  
✗ Never allow cross-customer queries  
✗ Never store plaintext secrets in code  

---

## Implementation Checklist

- [ ] Create users table with password_hash
- [ ] Create login endpoint: POST /api/auth/login
- [ ] Implement JWT generation & validation
- [ ] Add JWT authentication middleware to all APIs
- [ ] Filter queries by customer_id everywhere
- [ ] Store SP credentials in Key Vault
- [ ] Implement logout endpoint: DELETE /api/auth/logout
- [ ] Add audit logging for login events
- [ ] Test cross-customer isolation (A cannot see B's data)

