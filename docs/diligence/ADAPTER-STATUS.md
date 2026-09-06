# Adapter Status — Demo vs Hybrid vs Live

**Canonical labeling rules:** [CMS-0057-F-DEMO-MODE-LIVE-ADAPTERS.md](../compliance/CMS-0057-F-DEMO-MODE-LIVE-ADAPTERS.md)
**Machine-readable inventory:** `GET /fhir/r4/adapter-status`
**Response headers (every FHIR call):** `X-CHO-Adapter-Mode`, `X-CHO-Data-Class`, `X-CHO-Adapter-Label`

This table is the September 2026 **synthetic demo tenant** default. A live pilot overwrites it in config (`FhirAdapters` + `Appeals:UseMockAdapter`) so the endpoint cannot silently look broader than the wiring.

## Default demo tenant (`demo-tenant`, data class `synthetic`)

| Resource / capability | Mode | Source in repo | Buyer-safe wording |
| --- | --- | --- | --- |
| Patient | Demo | `MockFhirDataAdapter` | Demonstrates technical behavior with synthetic data. |
| Coverage | Demo | `MockFhirDataAdapter` | Demonstrates technical behavior with synthetic data. |
| Encounter | Demo | `MockFhirDataAdapter` | Demonstrates technical behavior with synthetic data. |
| Claim | Demo | `MockFhirDataAdapter` | Demonstrates technical behavior with synthetic data. |
| ExplanationOfBenefit | Hybrid | claims-service FHIR proxy | Pilot wiring in progress; source labels shown per resource. |
| Practitioner | Hybrid | provider-service FHIR proxy | Pilot wiring in progress; source labels shown per resource. |
| PractitionerRole | Hybrid | provider-service FHIR proxy | Pilot wiring in progress; source labels shown per resource. |
| Organization | Hybrid | provider-service FHIR proxy | Pilot wiring in progress; source labels shown per resource. |
| InsurancePlan | Hybrid | benefit-plan-service FHIR proxy | Pilot wiring in progress; source labels shown per resource. |
| Appeal | Demo (Hybrid when `Appeals:UseMockAdapter=false`) | mock or `HttpFhirAppealAdapter` | See endpoint. |
| Prior Authorization (PAS) | Demo | `PasAutoAdjudicator` + rule engine (TX seed rules if DB present); `Claim/$submit` and `Claim/$inquire`; CDex additional information (`Task` on the CDex Task Attachment Request profile + `$submit-attachment`) over the rfai-service case record; `PriorAuthorizationRetentionWorker` | Demonstrates technical behavior with synthetic data. Status inquiry projects the stored authorization record read-only; it never re-queries a payer or mutates state. A pended (X12 A4) decision that names what it needs raises a durable additional-information request the provider can retrieve and answer; accepted documentation returns the authorization to **review**, never to approved. Attachment bytes go through the shared `IClaimAttachmentContentStore` — fhir-service registers the **in-process** implementation by default, so a deployment must bind a durable one; malware scanning is a registered seam with no scanner behind it. Retention is policy-driven and tenant-scoped, purges only terminal records past their boundary, and is disabled until a deployment opts in. |
| CRD / DTR | Demo | `CrdService` / `DtrService` | Demonstrates technical behavior with synthetic data. |
| Bulk FHIR export | Demo | technical scaffold | Demonstrates technical behavior with synthetic data. |
| Consent | Demo | consent-service registry (`ConsentPurposeOfUse` + shared `ConsentAuthorizationPolicy`; PHI-free `authorization-snapshots` projection) | Demonstrates technical behavior with synthetic data. Purpose-scoped authorization is enforced server-side for both Payer-to-Payer (both directions) and Provider Access, through one shared evaluator and one policy. |
| USCDI clinical (`AllergyIntolerance`, `CarePlan`, `CareTeam`, `Condition`, `Device`, `DiagnosticReport`, `Goal`, `Immunization`, `MedicationDispense`, `MedicationRequest`, `Observation`, `Procedure`) | Demo | `ClinicalResourceController` over the Payer-to-Payer import store (`IClinicalResourceStore`); MongoDB when configured, in-process otherwise | Demonstrates technical behavior with synthetic data. Read and member-scoped search only, behind the same SMART / attribution / consent controls as the rest of the surface. Every served resource today is **imported from another payer**, labelled as such in `meta.source`; no CHO component authors native clinical data. Served as **valid FHIR R4 with no US Core profile claimed** — the content is not re-shaped to US Core invariants and is not validated against a profile. Search is `_id`, `patient`, and `subject` where R4 defines it; `category`, `code`, `status` and date search are not implemented and are not advertised. |
| Payer-to-Payer | Out of scope | Bulk FHIR + consent only | Not in current pilot scope. |
| Provider Access | Demo | `ProviderAccessAuthorizationService` + global `ProviderAccessAuthorizationFilter` | Demonstrates technical behavior with synthetic data. Authentication, SMART scope, provider/member attribution and an active purpose-scoped consent are each independent and mandatory. Attribution panels are configured, not fed from a live payer roster. |

Effective mode for the default tenant is **Hybrid**, because some resources proxy to other services while Patient / Coverage remain mock. Configured mode is **Demo**. The endpoint reports both so a reviewer can see the difference.

## How a pilot moves a resource to Live

1. Sign BAA.
2. Point the resource at the payer source system (typed HTTP adapter or the existing proxy).
3. Set `FhirAdapters:Resources:<Name>=Live` and `FhirAdapters:DataClassification` to the real class.
4. For appeals, set `Appeals:UseMockAdapter=false` and `Services:AppealsServiceUrl`.
5. Re-fetch `/fhir/r4/adapter-status`. If any in-scope resource is still Demo, **effective mode stays Hybrid**. That is intentional.

## Phrases that fail diligence

- Calling mock Patient data “live member records”
- Calling Bulk Export “payer-to-payer complete”
- Calling `/fhir/r4/compliance-status` a CMS attestation
- Stripping adapter headers in a screen share
