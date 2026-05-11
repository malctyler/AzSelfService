# ADR 0003: Local Authentication for MVP (Migrate to Entra ID B2C Post-MVP)

**Date:** May 11, 2026  
**Status:** ACCEPTED  
**Context:** Choosing authentication mechanism for MVP platform  

## Problem

AzSelfService needs user authentication. Options:

1. **Local Provider:** Store username/password in our database (bcrypt hashed)
2. **Azure Entra ID B2C:** Outsource to managed identity provider
3. **Entra ID (Tenant Directory):** Use customer's corporate Azure AD

**Tradeoff:** Speed to MVP vs. enterprise features

## Decision

**For MVP: Implement Local Provider authentication.**  
**Post-MVP: Migrate to Entra ID B2C.**

## Rationale

### 1. Simplicity & MVP Speed (Critical for MVP)

**Local Auth:**
- No external service setup required
- Works immediately with `docker-compose up`
- One table, one endpoint, JWT tokens
- ~1 day to implement
- Zero configuration needed locally

**B2C:**
- Requires B2C tenant provisioning (Azure)
- Requires app registration
- Requires MSAL SDK integration
- Requires redirect URI configuration
- ~2-3 days to implement
- Complex local dev setup

**Impact:** Local auth lets us focus on platform core (Terraform execution) rather than auth infrastructure.

### 2. Local Development Experience

**Scenario:** Developer clones repo, runs `docker-compose up`

**Local Auth:**
```bash
git clone ...
docker-compose up
# Backend ready at localhost:5000
# Login with test/password
```

**B2C:**
```bash
git clone ...
# Step 1: Create B2C tenant in Azure
# Step 2: Register app in B2C portal
# Step 3: Configure redirect URIs
# Step 4: Set environment variables
# Step 5: docker-compose up
# Login with B2C account
```

**Result:** Local auth enables faster developer onboarding.

### 3. MVP Scope Boundaries

**MVP goals:**
- ✓ Terraform execution works
- ✓ Job queue is reliable
- ✓ Module framework is extensible
- ✗ Enterprise authentication (B2C features)

**B2C features MVP doesn't need:**
- SSO (single sign-on)
- MFA (multi-factor auth)
- Password policy enforcement
- SAML federation
- Custom branding

**Post-MVP needs:** All of the above

**Result:** Local auth covers MVP requirements; B2C adds enterprise polish post-MVP.

### 4. No Lock-In (Clear Migration Path)

**Local → B2C migration:**

```
Phase 1: Deploy B2C alongside local auth
  - Users can "Login with Local Account" OR "Login with B2C"
  - Both return JWT tokens (compatible with API)

Phase 2: Migrate users to B2C
  - Customers move their users
  - Local auth still works as fallback

Phase 3: Deprecate local auth
  - Eventually disable local auth endpoint
  - All users on B2C
```

**Zero breaking changes, customer-driven timeline.**

### 5. Cost (MVP Efficiency)

**Local Auth:** $0 (built into app)  
**B2C:** $0–$0.50 per 100 authentications (minimal cost, actually cheap)

**But:** B2C infrastructure setup (tenant, app registration, SSO policies) requires time investment now.

---

## Local Auth Design

### Authentication Flow

```
POST /api/auth/login { username, password }
    ↓
Backend: bcrypt.verify(password, user.password_hash)
    ↓
If valid:
  JWT { user_id, customer_id, username, iat, exp }
  ↓
Frontend: localStorage.setItem("token", jwt)
  ↓
Subsequent requests: Authorization: Bearer {jwt}
    ↓
Backend: Validate JWT signature, extract claims
```

### Credentials

**Test user (seed data):**
```
username: admin
password: Test@1234 (hashed)
customer: Default customer
```

### Implementation

```csharp
[HttpPost("/api/auth/login")]
public async Task<IActionResult> Login(LoginRequest request)
{
    var user = await _db.Users.FirstOrDefaultAsync(u => u.Username == request.Username);
    
    if (user == null || !BCrypt.Net.BCrypt.Verify(request.Password, user.PasswordHash))
        return Unauthorized("Invalid username or password");
    
    var token = GenerateJWT(user);
    return Ok(new { token });
}

private string GenerateJWT(User user)
{
    var claims = new[]
    {
        new Claim("user_id", user.Id.ToString()),
        new Claim("customer_id", user.CustomerId.ToString()),
        new Claim("username", user.Username)
    };
    
    var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_config["Jwt:Secret"]));
    var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
    
    var token = new JwtSecurityToken(
        issuer: _config["Jwt:Issuer"],
        audience: _config["Jwt:Audience"],
        claims: claims,
        expires: DateTime.UtcNow.AddHours(24),
        signingCredentials: credentials
    );
    
    return new JwtSecurityTokenHandler().WriteToken(token);
}
```

---

## Post-MVP: Migration to B2C

### Why B2C (Not Entra ID Tenant)?

**Entra ID Tenant (Customer's Azure AD):**
- Con: Each customer has different Azure AD; federation complex
- Con: Not suitable for SaaS (each customer separate directory)

**B2C (Microsoft's SaaS Identity Service):**
- Pro: Single B2C tenant for all customers
- Pro: Built for multi-tenant SaaS
- Pro: Supports social identity (future)
- Pro: Native Azure integration

### B2C Migration Steps (Post-MVP)

1. **Create B2C Tenant:** `azselfservice.onmicrosoft.com`
2. **Create App Registration** in B2C
3. **Add MSAL SDK** to frontend
4. **Update Backend** to accept B2C tokens
5. **User migrations** over 4-week period
6. **Deprecate local auth** once all users migrated

### B2C Config (Post-MVP)

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

## Consequences

### Positive

✓ MVP launches 1-2 weeks faster  
✓ Zero friction for local development  
✓ Simpler debugging (auth is transparent)  
✓ No external dependency during MVP  
✓ Clear path to B2C (non-breaking)  
✓ Customers can start using platform immediately  

### Negative

✗ No enterprise SSO in MVP  
✗ No MFA in MVP  
✗ Manual password management (basic security)  
✗ Must implement B2C post-MVP (architectural debt)  
✗ Developers must remember test credentials  

---

## Alternatives Considered

### Alternative 1: B2C from Day 1

**Why considered:** Future-proof, enterprise-ready  
**Why rejected:** Delays MVP launch by 1-2 weeks; B2C features not needed for MVP; local auth sufficient for testing

### Alternative 2: No Authentication (MVP)

**Why considered:** Fastest possible MVP  
**Why rejected:** Security risk; no audit trail; unsuitable for production; B2C isn't that much slower

### Alternative 3: Azure AD (Customer Tenant)

**Why considered:** Integrates with customer's Azure  
**Why rejected:** Not multi-tenant friendly; complex federation; B2C is better for SaaS

---

## Implementation Notes

### MVP Checklist

- [ ] Create users table with password_hash (bcrypt)
- [ ] Create POST /api/auth/login endpoint
- [ ] Implement JWT generation (HS256)
- [ ] Add JWT validation middleware
- [ ] Create test user (admin/Test@1234)
- [ ] Test local auth in docker-compose
- [ ] Document password requirements (post-MVP: B2C enforces)

### Post-MVP Checklist (When B2C Migration Starts)

- [ ] Provision B2C tenant
- [ ] Create app registration
- [ ] Add MSAL SDK to frontend
- [ ] Deploy B2C sign-in/sign-up flows
- [ ] Update backend to accept B2C tokens
- [ ] Dual support (local + B2C auth)
- [ ] Migrate customers over 4 weeks
- [ ] Deprecate local auth

---

## Timeline

| Phase | Timeline | Task |
|-------|----------|------|
| MVP | Week 1-2 | Local auth only |
| Stabilization | Week 3-4 | Test, debug, stabilize |
| Enhancement | Week 5+ | Plan B2C migration |
| B2C Migration | Month 2-3 | Implement B2C, dual auth, migrate users |
| Deprecation | Month 4 | Disable local auth |

---

## References

- [Bcrypt .NET](https://github.com/BcryptNet/bcrypt.net-core)
- [JWT Authentication](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/jwt)
- [Azure B2C](https://learn.microsoft.com/en-us/azure/active-directory-b2c/)
- [MSAL.js](https://github.com/AzureAD/microsoft-authentication-library-for-js)

