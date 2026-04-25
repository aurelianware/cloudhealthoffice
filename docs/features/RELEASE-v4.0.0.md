> **Note:** This document references Azure Logic Apps, which were the original orchestration runtime. CHO has since migrated to Argo Workflows on AKS — see [ADR-004](../adr/004-remove-logic-apps.md) for details.

# v4.0.0 Release Notes - February 11, 2026

## 🎯 Major Milestones

This release represents a significant evolution of Cloud Health Office with **critical security hardening**, **multi-tenant SaaS readiness**, and **cloud portability infrastructure**.

---

## 🔒 Security Achievements

### Zero Vulnerabilities ✅
- **Started:** 86 high-severity vulnerabilities
- **Ended:** 0 vulnerabilities
- **Reduction:** 100%

### Fixed CVEs
1. **CVE-2024-43485**: System.Formats.Asn1 Remote Code Execution
   - Severity: High
   - Fix: 8.0.0 → 8.0.1
   - Impact: RCE in certificate parsing (Portal + all services)

2. **CVE-2024-21907**: Newtonsoft.Json Deserialization Attack
   - Severity: High
   - Fix: 10.0.2 → 13.0.3
   - Impact: All microservices using Cosmos DB SDK

### Security Infrastructure
- Added `Directory.Build.props` to enforce secure transitive dependencies
- Updated 59 direct package references
- Disabled Logic Apps deployment (migrated to Argo workflows)
- Configured PII/PHI scanner to allow test data patterns

---

## 🏢 Multi-Tenant SaaS Features

### Portal Isolation (CRITICAL)
- **TenantContextService**: Maps Azure AD tenant → CHO tenant via subscription lookup
- **TenantHttpMessageHandler**: Injects `X-Tenant-ID` header on all backend API calls
- **Backend Enforcement**: All 36 microservices scope database operations by tenant (`PartitionKey(tenantId)` or equivalent)
- **Logout Functionality**: Proper Microsoft Identity sign-out
- **Dynamic UI**: Shows actual tenant name with demo/production badges

### Impact
✅ **Prevents cross-tenant data leakage** (previously at risk)  
✅ Production-ready multi-tenant isolation  
✅ Supports unlimited tenant onboarding  

---

## ☁️ Cloud Portability (Feature Branch)

### Multi-Cloud Infrastructure
Created `CloudHealthOffice.Infrastructure` package supporting:
- **Azure**: Cosmos DB (current production)
- **DigitalOcean**: MongoDB (65% cost savings)

### Cost Comparison
| Cloud | Monthly Cost | Use Case |
|-------|-------------|----------|
| Azure only | $640 | Enterprise production |
| DigitalOcean only | $225 | Cost-conscious startups |
| Both clouds | $865 | Dev on DO, prod on Azure |

### Status
🚧 Available in `feature/multi-cloud-infrastructure` branch  
📋 Waiting for testing before merge to main  
✅ Reference implementation: member-service compiles successfully  

---

## 🔧 Infrastructure Updates

### Fixed Azure Permissions
- Added `Application Administrator` role (manage app registrations)
- Added `User Access Administrator` role (assign RBAC permissions)
- Resolves deployment permission errors

### SFTP Integration
- **New Tenant:** clouddentaloffice
- **Endpoint:** 20.115.193.245:22 (sftp.cloudhealthoffice.com pending DNS)
- **Password:** Stored in Azure Key Vault
- **Folder Structure:** /dental-claims/inbound/837/, /outbound/835/, /outbound/277/

---

## 📦 Package Updates

### Updated Dependencies (59 packages)
- Azure.Identity: 1.12.1 → 1.13.1
- Azure.Core: 1.42.0 → 1.44.1
- Microsoft.Azure.Cosmos: 3.42.0 → 3.45.0
- MudBlazor: 7.20.0 → 8.4.0
- Stripe.net: 46.4.0 → 47.0.0
- Swashbuckle.AspNetCore: 6.5.0 → 10.1.2
- (50+ additional packages updated for security)

---

## 🎨 Branding

### Cloud Health Office Identity
- **Logo:** The Sentinel (obsidian monolith shield)
- **Primary Asset:** `docs/images/logo-cloudhealthoffice-sentinel-primary.png`
- **Colors:** Absolute black background, chromatic circuit highlights
- **Tone:** Authoritative, future-focused (2047 aesthetic)

---

## ⚠️ Breaking Changes

### Logic Apps Deployment Disabled
- **Reason:** Migrated to Argo workflows for better GitOps
- **Impact:** `deploy.yml` no longer deploys Logic Apps
- **Action Required:** Use Argo for X12 workflow updates

### Multi-Tenant Backend Requirement
- **Change:** Portal now sends `X-Tenant-ID` header on all API calls
- **Impact:** Backend services **must** have TenantMiddleware registered
- **Verification:** All 36 microservices already compliant ✅

---

## 🚀 Deployment

### GitHub Actions Workflows
1. **docker-build.yml**: ✅ Passing (builds container images for the core service set)
2. **deploy.yml**: ✅ Passing (Azure AKS deployment)
3. **pre-approval-checks.yml**: ✅ Passing (security gates)
4. **deploy-multi-cloud.yml**: 🚧 Feature branch (cloud toggles)

### Kubernetes Deployments
- **Namespace:** cloudhealthoffice
- **Cluster:** cho-aks-prod (Azure East US)
- **Registry:** ghcr.io/aurelianware/cloudhealthoffice-*
- **Replicas:** 2 per service (for HA)

---

## 📊 Metrics

### Codebase Health
- **Services:** 36 microservices (all .NET 8)
- **Portal:** Blazor Server with MudBlazor UI
- **Database:** Azure Cosmos DB (SQL API)
- **Total Projects:** 20+ (.csproj files)
- **Vulnerabilities:** 0 ✅

### Test Coverage
- **X12 Workflows:** 270, 271, 276, 277, 278 (prior auth), 834, 835, 837
- **FHIR R4:** Partial implementation (certification prep ongoing)
- **SFTP:** Integration tested with clouddentaloffice tenant

---

## 🏆 Production Readiness

| Component | Status |
|-----------|--------|
| Multi-Tenant Isolation | ✅ **READY** |
| Security Hardening | ✅ **COMPLETE** |
| HIPAA Controls | ✅ **ENFORCED** |
| Azure AD Integration | ✅ **WORKING** |
| SFTP Trading Partners | ✅ **OPERATIONAL** |
| Vulnerability Scanning | ✅ **CLEAN** |
| Cloud Portability | 🚧 **TESTING** |

---

## 🎯 Known Issues

1. **DNS Configuration:** sftp.cloudhealthoffice.com not yet pointed to 20.115.193.245
2. **Mock Data Fallback:** Portal shows mock data when backend unavailable (configurable via `Portal.UseMockDataFallback`)
3. **Stripe.net Warning:** NU1603 - Package 46.4.0 not found, resolved to 47.0.0 (non-breaking)

---

## 📅 Roadmap

### Q1 2026 (Next 3 Months)
- ✅ Multi-tenant portal isolation **(DONE in v4.0.0)**
- ✅ Security vulnerability cleanup **(DONE in v4.0.0)**
- 🚧 Multi-cloud infrastructure rollout (targeting Q1 end)
- 📋 FHIR R4 certification prep (CMS deadline: Jan 1, 2027)
- 📋 SaaS launch: Terms of Service, tenant admin dashboard
- 📋 Monitoring & alerting infrastructure

### Q2 2026
- FHIR endpoints production deployment
- DigitalOcean production environment (cost savings)
- Interactive secret setup wizard
- Penetration testing

---

## 🙏 Contributors

This release was made possible by focused engineering on:
- Security hardening (100% vulnerability reduction)
- Multi-tenant SaaS architecture
- Cloud portability infrastructure
- SFTP integration for trading partners

---

## 📚 Documentation

- [README.md](README.md) - Getting started
- [QUICKSTART.md](QUICKSTART.md) - Rapid deployment
- [DEPLOYMENT.md](DEPLOYMENT.md) - Production deployment guide
- [SECURITY.md](SECURITY.md) - HIPAA controls
- [MULTI-CLOUD-SETUP.md](MULTI-CLOUD-SETUP.md) - Cloud portability guide
- [MULTI-CLOUD-DEPLOYMENT-GUIDE.md](MULTI-CLOUD-DEPLOYMENT-GUIDE.md) - Operations runbook

---

## 🔗 Links

- **Repository:** https://github.com/aurelianware/cloudhealthoffice
- **Issues:** https://github.com/aurelianware/cloudhealthoffice/issues
- **Security:** https://github.com/aurelianware/cloudhealthoffice/security
- **Portal:** https://portal.cloudhealthoffice.com

---

**Release Date:** February 11, 2026  
**Git Tag:** v4.0.0  
**Commit:** main branch (863821d)
