# Security One-Pager — Cloud Health Office Layer 1 Pilot

**Status as of:** September 2026
**Audience:** payer CISO / security architecture review
**Scope:** CMS-0057-F Compliance Accelerator (Layer 1), founding-partner pilot

This is a current-state architecture note. It is not a SOC 2 Type II, HITRUST, or pentest letter. Those artifacts are produced per pilot, not claimed as already in hand.

## What you are reviewing

A Kubernetes-native FHIR / SMART / prior-authorization / audit layer that deploys **beside** an existing core admin system. The pilot default is a **synthetic demo tenant**. Production PHI is out of scope until a BAA is signed and the adapter-status report shows Live (or Hybrid with named live resources) for the resources in play.

## Controls that exist in the repository today

| Control | How it is implemented | Evidence |
| --- | --- | --- |
| Tenant isolation | `TenantMiddleware` on services; Cosmos / Mongo partition by `tenantId` on migrated containers; Kafka topic namespacing pattern | claims 5.1b partition migration; service middleware |
| AuthN / AuthZ | SMART-on-FHIR JWT bearer via `smart-auth-service`; resource-type scope enforcement in `fhir-service` | `SmartScopeEnforcementMiddleware`; patient-binding tests |
| Secrets | Azure Key Vault configuration provider; no production secrets in git | `AddAzureKeyVaultConfiguration`; `SECURITY.md` |
| Encryption in transit | TLS on ingress; service-mesh / cluster policy is a deployment choice | Helm / AKS path |
| Encryption at rest | Store and Key Vault managed keys on Azure deployments; field-level encryption on appeals PHI | appeals-service (Layer 2, already shipped) |
| PHI in telemetry | OpenTelemetry with PHI-scrubbing span processor | Infrastructure observability |
| Audit | Request tenant + correlation headers; authorization and consent services emit events | correlation / tenant propagation handlers |
| Dependency scanning | Dependabot + CodeQL in GitHub Actions | `.github/workflows` |
| License / production use | BSL 1.1; production PHI or live operations require a commercial license | `COMMERCIAL-LICENSING.md` |

## What is explicitly not claimed

| Item | Status |
| --- | --- |
| Independent penetration test letter | Not on file. Scoped as a pilot deliverable or customer-funded exercise. |
| SOC 2 Type II / HITRUST | Not on file. |
| Production reference customer | None. |
| Tested backup / DR for a named RTO/RPO | Designed; not a completed customer DR test. |
| Consumer self-serve identity (Google / email OTP) | Not wired. Pilot identity is Entra ID / SMART issuer configured per tenant. |
| “Zero vulnerabilities” | False. Dependencies are scanned; residual findings are tracked, not advertised as zero. |

## Tenant and data-class model

1. **Synthetic (default).** Demo tenant `demo-tenant`. No PHI. Adapter mode Demo or Hybrid.
2. **Limited PHI (after BAA).** Named extract, named environment, named operators.
3. **Production PHI.** Only after BAA, security review, adapter-status Live/Hybrid for in-scope resources, and commercial license.

Adapter inventory is machine-readable:

```http
GET /fhir/r4/adapter-status
X-CHO-Adapter-Mode: Hybrid
X-CHO-Data-Class: synthetic
X-CHO-Adapter-Label: Pilot wiring in progress; source labels shown per resource.
```

## Deployment shape we will agree in week 2

- Customer-managed AKS (or equivalent Kubernetes) **or** a CHO-hosted sandbox with no PHI.
- Private cluster endpoints. No public FHIR without SMART.
- Customer identity issuer registered in `smart-auth-service`.
- Break-glass and admin access listed; standing privileged access is a finding, not a design goal.

## Open items a CISO should put on the pilot RAID log

1. Pen test / vulnerability assessment schedule and who pays.
2. Log retention (HIPAA suggests 6 years for some documentation; confirm with counsel).
3. Incident contacts and after-hours path — see [INCIDENT-AND-SUPPORT.md](INCIDENT-AND-SUPPORT.md).
4. Subprocessor list for the chosen cloud (Azure default).
5. Whether CHO staff will ever see PHI (default: no; customer operates the cluster).

## Related

- [DATA-HANDLING.md](DATA-HANDLING.md)
- [BAA-TEMPLATE.md](BAA-TEMPLATE.md)
- [docs/security/SECURITY-HARDENING.md](../security/SECURITY-HARDENING.md)
- [SECURITY.md](../../SECURITY.md)
