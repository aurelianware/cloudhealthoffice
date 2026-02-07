---
name: "[v4.0] Production Security Hardening"
about: Implement Azure Key Vault, WAF, and TLS for production HIPAA compliance
title: "[v4.0] Implement Production Security Hardening with Key Vault, WAF, and TLS"
labels: enhancement, security, v4.0, priority-high
assignees: aurelianware
---

## Overview
As part of v4.0 SaaS launch, enhance security to meet production HIPAA standards. Build on existing security automation (CVE scanning via Dependabot) and integrate with new tenant management for tenant-specific secrets.

## Objectives
- ✅ Replace hard-coded secrets with Azure Key Vault
- ✅ Deploy WAF for portal and API protection
- ✅ Enforce TLS 1.3+ across all endpoints
- ✅ Implement RBAC with Azure AD integration
- ✅ Enable comprehensive audit logging

## Implementation Steps

### 1. Azure Key Vault Integration
**Goal:** Replace hard-coded secrets in microservices with Managed Identity pulls from Key Vault

**Tasks:**
- [ ] Create Azure Key Vault instance for production
- [ ] Enable Managed Identity for all microservices in AKS
- [ ] Migrate secrets to Key Vault with tenant-scoped prefixes:
  - `tenant-{id}-db-connection` (Cosmos DB connection strings)
  - `tenant-{id}-stripe-key` (per-tenant Stripe keys)
  - `tenant-{id}-clearinghouse-creds` (SFTP credentials)
  - `global-cosmos-key` (shared Cosmos DB primary key)
  - `global-stripe-webhook-secret` (Stripe webhook signing)
- [ ] Update all microservices to use `Azure.Identity` SDK:
  - Claims Service
  - Eligibility Service
  - Tenant Service
  - Coverage Service
  - Authorization Service
  - Provider Service
- [ ] Remove hard-coded secrets from `appsettings.json` files
- [ ] Update Kubernetes deployments to use pod identity annotations
- [ ] Add fallback to local secrets for development (Cosmos Emulator)

**Code Example:**
```csharp
// Program.cs - Key Vault integration
builder.Configuration.AddAzureKeyVault(
    new Uri($"https://{builder.Configuration["KeyVault:VaultName"]}.vault.azure.net/"),
    new DefaultAzureCredential());
```

### 2. Web Application Firewall (WAF)
**Goal:** Configure Azure Front Door or Application Gateway WAF for portal and APIs

**Tasks:**
- [ ] Deploy Azure Application Gateway with WAF v2 SKU
- [ ] Configure OWASP Core Rule Set 3.2 (protection against):
  - SQL injection
  - Cross-site scripting (XSS)
  - Local file inclusion
  - Remote file inclusion
  - Session fixation
- [ ] Set custom WAF rules:
  - Rate limiting: 1000 requests/5min per IP
  - Geo-blocking: allow only US/Canada for beta
  - Bot protection: block known malicious user agents
- [ ] Route Blazor portal through WAF (portal.cloudhealthoffice.com)
- [ ] Route all API microservices through WAF (api.cloudhealthoffice.com)
- [ ] Configure health probe exclusions for `/health` and `/ready` endpoints
- [ ] Test with OWASP ZAP automated scans
- [ ] Set up alerts for WAF blocks (threshold: >100 blocks/hour)

**Architecture:**
```
Internet → Azure Front Door/WAF → AKS Ingress → Services
```

### 3. TLS/HTTPS Enforcement
**Goal:** Require TLS 1.3+ with automated certificate management

**Tasks:**
- [ ] Deploy cert-manager in AKS for automated cert renewal
- [ ] Integrate with Azure Certificate Manager or Let's Encrypt
- [ ] Update Ingress manifests to require TLS:
  ```yaml
  annotations:
    nginx.ingress.kubernetes.io/ssl-redirect: "true"
    nginx.ingress.kubernetes.io/force-ssl-redirect: "true"
  tls:
  - hosts:
    - portal.cloudhealthoffice.com
    secretName: cloudhealthoffice-tls
  ```
- [ ] Add HSTS headers in .NET middleware:
  ```csharp
  app.UseHsts(); // Strict-Transport-Security: max-age=31536000
  ```
- [ ] Configure minimum TLS version in Application Gateway: TLS 1.3
- [ ] Disable older cipher suites (no TLS 1.0/1.1, no weak ciphers)
- [ ] Test with SSL Labs (aim for A+ rating)
- [ ] Update Kafka/Cosmos DB connections to use TLS

### 4. RBAC with Azure AD
**Goal:** Integrate tenant management with Azure AD for role-based access

**Tasks:**
- [ ] Register Cloud Health Office app in Azure AD
- [ ] Define app roles:
  - `TenantAdmin` (full tenant management)
  - `TenantUser` (read-only tenant data)
  - `SystemAdmin` (cross-tenant operations)
  - `Developer` (API access with scopes)
- [ ] Integrate tenant-service with Azure AD:
  - Map Azure AD users to tenants in Cosmos DB
  - Validate JWT tokens with `[Authorize]` attributes
  - Enforce role-based access in controllers
- [ ] Add tenant context to Azure AD tokens (custom claim: `tenant_id`)
- [ ] Update portal authentication to use Azure AD B2C
- [ ] Document RBAC model in SECURITY.md

### 5. Audit Logging
**Goal:** Enable comprehensive audit trails to Azure Monitor

**Tasks:**
- [ ] Deploy Azure Log Analytics workspace
- [ ] Enable diagnostic logs for:
  - AKS cluster (API server, node logs)
  - Application Gateway (WAF events, access logs)
  - Cosmos DB (data plane operations)
  - Key Vault (secret access)
- [ ] Add structured logging to all microservices:
  ```csharp
  logger.LogInformation("Tenant {TenantId} accessed resource {ResourceId} by user {UserId}",
      tenantId, resourceId, userId);
  ```
- [ ] Create Azure Monitor alerts:
  - Unauthorized Key Vault access attempts
  - Failed authentication (>10/min)
  - WAF blocks (>100/hour)
  - Anomalous API usage patterns
- [ ] Build Azure Workbook dashboard for security events
- [ ] Configure 90-day retention for audit logs (HIPAA requirement)

### 6. Stripe Secrets Integration
**Goal:** Store Stripe API keys in Key Vault with tenant context

**Tasks:**
- [ ] Store Stripe secrets in Key Vault:
  - `stripe-global-secret-key` (platform account)
  - `stripe-global-webhook-secret`
  - `tenant-{id}-stripe-connected-account` (for future connected accounts)
- [ ] Update tenant-service to retrieve Stripe key from Key Vault
- [ ] Ensure tenant-specific Stripe events include audit logging

## Tech Stack
- **Secrets:** Azure Key Vault + Azure.Identity SDK (.NET 8)
- **WAF:** Azure Application Gateway WAF v2
- **TLS:** cert-manager + Let's Encrypt / Azure Certificate Manager
- **RBAC:** Azure AD + Microsoft.Identity.Web
- **Logging:** Azure Monitor + Application Insights
- **Scanning:** OWASP ZAP + GitHub Advanced Security

## Testing

### Security Tests
- [ ] Penetration testing with OWASP ZAP:
  ```bash
  docker run -t owasp/zap2docker-stable zap-baseline.py \
    -t https://portal.cloudhealthoffice.com
  ```
- [ ] Verify secrets rotation (manually rotate Key Vault secret, confirm pods restart)
- [ ] Test WAF rules (simulate SQL injection, XSS attacks)
- [ ] SSL Labs test (https://www.ssllabs.com/ssltest/)
- [ ] Verify HSTS headers with curl: `curl -I https://portal.cloudhealthoffice.com`
- [ ] Test unauthorized access (expect 403 for missing roles)

### E2E Tests
- [ ] Create tenant → verify Key Vault secret created
- [ ] API call with valid JWT → success
- [ ] API call with invalid JWT → 401 Unauthorized
- [ ] Exceed rate limit → WAF blocks with 429
- [ ] Access audit logs → verify events captured

## Dependencies
- ✅ Tenant Management Service (deployed)
- ⏳ Azure subscription with Key Vault, Application Gateway quotas
- ⏳ Azure AD tenant for RBAC

## Documentation Updates
- [ ] Update [SECURITY.md](../../SECURITY.md) with:
  - Key Vault architecture diagram
  - WAF rule documentation
  - TLS configuration guide
  - RBAC role definitions
  - Audit log query examples
- [ ] Add [docs/KEYVAULT-SETUP.md](../../docs/KEYVAULT-SETUP.md) for secret migration
- [ ] Update [DEPLOYMENT.md](../../DEPLOYMENT.md) with WAF deployment steps

## Success Criteria
- ✅ Zero hard-coded secrets in production (all from Key Vault)
- ✅ WAF blocking >95% of simulated attacks
- ✅ SSL Labs rating: A+ for all domains
- ✅ All API calls require valid Azure AD tokens
- ✅ Audit logs capturing 100% of sensitive operations
- ✅ Dependabot + OWASP ZAP scans passing weekly

## Timeline
- **Week 1:** Key Vault integration (migrate all secrets)
- **Week 2:** WAF deployment and rule tuning
- **Week 3:** TLS enforcement and testing
- **Week 4:** RBAC integration and audit logging

**Total:** 4 weeks (1 FTE)

## References
- [Azure Key Vault Best Practices](https://learn.microsoft.com/en-us/azure/key-vault/general/best-practices)
- [HIPAA on Azure](https://learn.microsoft.com/en-us/azure/compliance/offerings/offering-hipaa-us)
- [OWASP Top 10](https://owasp.org/www-project-top-ten/)
- [SAAS-LAUNCH-READINESS.md](../../SAAS-LAUNCH-READINESS.md) - Priority 2 tasks
