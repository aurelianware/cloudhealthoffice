# Cloud Health Office

**Open-Source Core Administration Processing System (CAPS) for Health Plans**

A cloud-native, multi-tenant payer platform with FHIR R4 APIs, X12 EDI processing, claims scrubbing, and CMS-0057-F compliance built on .NET 8, Kubernetes, and Argo Workflows.

[![License: Apache 2.0](https://img.shields.io/badge/License-Apache_2.0-blue.svg)](https://opensource.org/licenses/Apache-2.0)
[![Version](https://img.shields.io/badge/version-v4.0.0-blue)](https://github.com/aurelianware/cloudhealthoffice/releases)
[![.NET](https://img.shields.io/badge/.NET-8.0-purple)](https://dotnet.microsoft.com)
[![FHIR](https://img.shields.io/badge/FHIR-R4-orange)](https://hl7.org/fhir/R4/)
[![CMS-0057-F](https://img.shields.io/badge/CMS--0057--F-Compliant-green)](./api/quickstarts/cms-0057f-compliance-quickstart.md)

---

## Architecture

```
                       ┌──────────────────────┐
                       │   Portal (Blazor)     │
                       └──────────┬───────────┘
                                  │
                       ┌──────────┴───────────┐
                       │  Argo Workflows +     │
                       │  Argo Events          │
                       └──────────┬───────────┘
       ┌──────┬──────┬────────────┼────────────┬──────┬──────┐
       │      │      │            │            │      │      │
   Claims  Elig-  Enroll-    Authorize    Provider Member Payment
   Service ibility ment      Service     Service  Svc   Service
       │      │      │            │            │      │      │
       └──────┴──────┴────────────┴────────────┴──────┴──────┘
                          Cosmos DB / MongoDB
```

**16 microservices** covering claims, eligibility, enrollment, authorization, attachments, appeals, member, provider, payment, benefit plans, coverage, reference data, sponsors, tenants, and trading partners. See [Architecture](docs/architecture/ARCHITECTURE.md).

---

## Key Capabilities

**CMS-0057-F FHIR R4 APIs** — Patient Access, Provider Access, Payer-to-Payer, Prior Auth, CDS Hooks. [OpenAPI specs](api/openapi/) and [compliance quickstart](api/quickstarts/cms-0057f-compliance-quickstart.md).

**X12 EDI Processing** — 270/271 eligibility, 275 attachments, 276/277 claim status, 278 prior auth, 834 enrollment, 835 remittance, 837P/I/D claims. Bidirectional X12-to-FHIR mapping.

**Claims Scrubbing** — 30+ pre-adjudication validation rules with provider pre-edit suggestions. Providers see payer edits before submitting. [Claims scrubbing quickstart](api/quickstarts/claims-scrubbing-quickstart.md).

**Multi-Tenant SaaS** — Tenant isolation, self-service signup, Stripe billing, per-tenant rule configuration.

**Argo Workflows** — Kubernetes-native orchestration for X12 file processing, claims pipelines, and tenant onboarding.

---

## Quick Start

```bash
git clone https://github.com/aurelianware/cloudhealthoffice.git
cd cloudhealthoffice
docker-compose up -d
```

Portal: `http://localhost:5000`
FHIR Metadata: `http://localhost:3000/fhir/r4/metadata`
Claims Scrubbing: `http://localhost:3100/api/v1/claims/validate`

See [Quickstart Guide](docs/onboarding/QUICKSTART.md) for detailed setup.

---

## Project Structure

```
cloudhealthoffice/
├── api/                    OpenAPI specs, quickstarts, Postman collection
├── config/                 Configuration schemas and examples
├── containers/             Container images (x12-parser, sftp-fetcher, etc.)
├── core/                   Shared type definitions and validation
├── docs/                   All documentation (architecture, deployment, security)
├── infrastructure/         Argo, K8s, Helm, Azure Bicep, Kafka, monitoring
├── schemas/                X12 XSD schemas
├── scripts/                Setup, deploy, testing, migration scripts
├── services/               .NET microservices (16 bounded contexts)
├── site/                   Marketing website
├── src/                    FHIR APIs, Portal, AI engine, Security, Functions
├── tests/                  Unit, integration, E2E tests + fixtures
└── tools/                  Migration wizard
```

---

## Technology Stack

- **.NET 8** / **TypeScript** — Microservices and FHIR APIs
- **Blazor Server** — Portal UI
- **Cosmos DB** / **MongoDB** — Multi-tenant data
- **Kubernetes** + **Argo Workflows** — Orchestration
- **Kafka** — Event streaming
- **Azure** — Cloud deployment (Bicep IaC)
- **SMART on FHIR** / **OAuth 2.0** — Authentication
- **Stripe** — SaaS billing

---

## Documentation

| Topic | Link |
|-------|------|
| Architecture | [docs/architecture/ARCHITECTURE.md](docs/architecture/ARCHITECTURE.md) |
| CMS-0057-F Compliance | [api/quickstarts/cms-0057f-compliance-quickstart.md](api/quickstarts/cms-0057f-compliance-quickstart.md) |
| Claims Scrubbing | [api/quickstarts/claims-scrubbing-quickstart.md](api/quickstarts/claims-scrubbing-quickstart.md) |
| Quickstart | [docs/onboarding/QUICKSTART.md](docs/onboarding/QUICKSTART.md) |
| Deployment | [docs/deployment/DEPLOYMENT.md](docs/deployment/DEPLOYMENT.md) |
| Security | [SECURITY.md](SECURITY.md) |
| API Reference | [api/openapi/](api/openapi/) |
| Roadmap | [docs/roadmap/ROADMAP.md](docs/roadmap/ROADMAP.md) |

---

## Related Projects

- **[Cloud Dental Office](https://github.com/aurelianware/clouddentaloffice)** — Provider-side dental practice management (Blazor, .NET 8, microservices)
- Together, CHO + CDO provide complete provider-to-payer interoperability

---

## License

[Apache License 2.0](LICENSE) — Copyright 2025 Aurelianware, Inc.
