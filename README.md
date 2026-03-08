<div align="center">

![Cloud Health Office](docs/images/logo-cloudhealthoffice-sentinel-primary.svg)

# Cloud Health Office

**Cloud-native integration platform for healthcare payers.**

Modern claims adjudication, X12 EDI processing, FHIR R4 APIs, and CMS-0057-F compliance —
without replacing your core administration system.

[![Version](https://img.shields.io/badge/version-v4.0.0-blue)](https://github.com/aurelianware/cloudhealthoffice/releases/tag/v4.0.0)
[![Tests](https://img.shields.io/badge/tests-531%20passing-brightgreen)](./tests/)
[![Coverage](https://img.shields.io/badge/coverage-85.93%25-green)](https://codecov.io/gh/aurelianware/cloudhealthoffice)
[![Security](https://img.shields.io/badge/vulnerabilities-0-brightgreen)](./SECURITY.md)
[![License](https://img.shields.io/badge/license-BSL%201.1-orange.svg)](./LICENSE)

[Website](https://cloudhealthoffice.com) · [Demo Portal](https://portal.cloudhealthoffice.com) · [API Docs](https://api.cloudhealthoffice.com) · [Contact Sales](mailto:sales@cloudhealthoffice.com)

</div>

---

## What This Is

Cloud Health Office is a payer integration platform that sits alongside your existing core admin system (QNXT, FACETS, HealthEdge, or any other). It handles the modern workloads that legacy platforms weren't built for: real-time EDI, FHIR APIs, medical attachment workflows, prior authorization automation, and CMS-0057-F compliance.

Start by integrating. Expand workload by workload. Every component is designed to either augment or replace the corresponding module in your core system — your choice, your timeline.

## Architecture

```
┌──────────────────────────────────────────────────────────────────┐
│                        API Gateway (YARP)                        │
├──────────┬───────────┬───────────┬───────────┬──────────────────┤
│  Claims  │ Eligiblty │   Auth    │  Benefit  │    Provider      │
│ Service  │  Service  │  Service  │  Engine   │    Service       │
│ (837/835)│ (270/271) │   (278)   │           │                  │
├──────────┴───────────┴───────────┴───────────┴──────────────────┤
│              Argo Workflows — Adjudication DAG                   │
│  ┌─────────┐ ┌──────────┐ ┌──────────┐ ┌─────────┐ ┌────────┐ │
│  │ Verify  │→│ Validate │→│   Get    │→│  Price  │→│  Pay   │ │
│  │Coverage │ │ Provider │ │ Benefits │ │  Claim  │ │ Claim  │ │
│  └─────────┘ └──────────┘ └──────────┘ └─────────┘ └────────┘ │
├──────────────────────────────────────────────────────────────────┤
│  Argo Events — SFTP polling, Kafka triggers, EDI ingest          │
├──────────────────────────────────────────────────────────────────┤
│  X12 Parsers (Python) │ FHIR Mappers (TS) │ 999/277 Gen (.NET)  │
├──────────────────────────────────────────────────────────────────┤
│              MongoDB / Cosmos DB — Multi-tenant                   │
└──────────────────────────────────────────────────────────────────┘
```

## Platform

| Component | Count | Details |
|-----------|-------|---------|
| Microservices | 17 | C# / .NET 8, multi-tenant, Cosmos + MongoDB dual-repo |
| X12 Parsers | 5 | 275, 276, 277, 278 (Python), 834 (Node.js) |
| FHIR APIs | 5 | Patient Access, Provider Access, Payer-to-Payer, Prior Auth, Provider Directory |
| Argo Workflows | 17 | Claims adjudication, EDI ingest, enrollment import, RFAI |
| Portal Pages | 37 | Blazor Server + MudBlazor, Azure AD B2C auth |
| CI/CD Workflows | 20 | GitHub Actions — build, test, deploy, security scan |
| Claims Scrubbing | 20+ rules | Data completeness, ICD-10/CPT format, NPI Luhn, POS, filing limits |

### Services

| Service | Purpose | X12 Transactions |
|---------|---------|-----------------|
| claims-service | Claim lifecycle and adjudication | 837P/I/D, 835 |
| claims-scrubbing-service | Pre-adjudication validation engine | 837 inbound |
| eligibility-service | Real-time and batch eligibility | 270/271 |
| authorization-service | Prior auth management | 278 |
| attachment-service | Medical attachment correlation, RFAI, 824 ack | 275, 277, 824 |
| enrollment-import-service | 834 enrollment processing | 834 |
| benefit-plan-service | Plan configuration and benefit rules | — |
| coverage-service | Member coverage and accumulators | — |
| member-service | Demographics and subscriber hierarchy | — |
| sponsor-service | Employer group management | — |
| provider-service | Provider and network management | — |
| payment-service | Check/EFT generation | 835 |
| appeals-service | Appeal submission and tracking | — |
| trading-partner-service | Clearinghouse configuration | — |
| tenant-service | Multi-tenant provisioning | — |
| reference-data-service | ICD-10, CPT, CARC/RARC code sets | — |

### CMS-0057-F Compliance

Cloud Health Office achieves full compliance with the CMS Interoperability and Prior Authorization Final Rule, 18 months ahead of the January 2027 deadline.

| Requirement | Implementation |
|-------------|---------------|
| Patient Access API | FHIR R4 — Claim, EOB, Coverage, Patient, Encounter |
| Provider Access API | FHIR R4 — Claim, EOB, Patient, Condition, Observation |
| Payer-to-Payer API | FHIR R4 — bidirectional member data exchange |
| Prior Authorization API | FHIR R4 + CDS Hooks — real-time PA decisions |
| Provider Directory API | FHIR R4 — Practitioner, Organization, Location, Network |
| USCDI v1/v2 | US Core profiles, Da Vinci Implementation Guides |

## Quick Start

**Prerequisites:** Docker, .NET 8 SDK

```bash
# Clone
git clone https://github.com/aurelianware/cloudhealthoffice.git
cd cloudhealthoffice

# Build and run
docker-compose up -d

# Verify
curl http://localhost:5000/health
```

Or deploy to Azure with one click:

[![Deploy to Azure](https://aka.ms/deploytoazurebutton)](https://portal.azure.com/#create/Microsoft.Template/uri/https%3A%2F%2Fraw.githubusercontent.com%2Faurelianware%2Fcloudhealthoffice%2Fmain%2Finfrastructure%2Fazure%2Fmain.json)

## Project Structure

```
cloudhealthoffice/
├── src/
│   ├── services/           # 17 C# microservices
│   ├── engines/            # Calculation engines (Benefit Engine)
│   ├── portal/             # Blazor Server portal (MudBlazor)
│   ├── site/               # Marketing site (cloudhealthoffice.com)
│   ├── fhir/               # FHIR R4 APIs and X12→FHIR mappers
│   ├── api-docs/           # Swagger UI (api.cloudhealthoffice.com)
│   ├── ai/                 # AI-assisted EDI resolution
│   └── tools/              # Migration wizard
├── infrastructure/
│   ├── argo-workflows/     # 17 workflow DAGs
│   ├── argo-events/        # SFTP, Kafka, EDI event sources
│   ├── azure/              # Bicep IaC
│   ├── helm/               # Helm chart
│   ├── k8s/                # Kubernetes manifests
│   ├── monitoring/         # Grafana + Prometheus
│   └── logicapps/          # Azure Logic Apps (attachment workflows)
├── containers/             # Sidecar containers (parsers, encoders, SFTP)
├── schemas/                # X12 XSD schemas, JSON schemas (auth, appeals)
├── tests/                  # Unit, integration, E2E, fixtures
├── docs/                   # Architecture, ADRs, guides, features
├── scripts/                # Deploy, setup, CLI tools
├── config/                 # Configuration schemas and examples
├── core/                   # Shared types and validation
└── api/                    # OpenAPI specs and quickstarts
```

## Tech Stack

**Backend:** C# / .NET 8, Python 3.11, Node.js 20
**Frontend:** Blazor Server, MudBlazor, Azure AD B2C
**Data:** MongoDB / Azure Cosmos DB (dual-repository pattern)
**Orchestration:** Argo Workflows + Argo Events (Kubernetes-native)
**Infrastructure:** Azure (AKS, ACR, Key Vault, Cosmos DB), Helm, Bicep IaC
**EDI:** X12 5010 (270/271, 275, 276/277, 278, 834, 835, 837, 824, 999)
**FHIR:** R4, US Core, Da Vinci IG (PDex, PAS, CRD, DTR, HRex)
**CI/CD:** GitHub Actions (20 workflows), Codecov, Dependabot, Gitleaks

## Deployment Options

| Option | Best For | Time to Production |
|--------|----------|-------------------|
| **Azure Logic Apps** | Azure-first orgs, minimal ops overhead | < 1 hour |
| **Kubernetes (AKS/EKS/GKE)** | Enterprise control, multi-cloud, hybrid | 1–2 hours |
| **Docker Compose** | Development, evaluation, POC | 5 minutes |

## Documentation

| Document | Description |
|----------|-------------|
| [Quickstart](docs/guides/QUICKSTART.md) | Get running in 5 minutes |
| [Architecture](docs/guides/ARCHITECTURE.md) | System design and service interactions |
| [Deployment Guide](docs/guides/DEPLOYMENT.md) | Production deployment for Azure and Kubernetes |
| [CMS-0057-F Compliance](docs/features/CMS-0057-F-COMPLIANCE.md) | Regulatory compliance mapping |
| [Claims Pipeline](docs/features/837-CLAIMS-PIPELINE.md) | End-to-end claims adjudication flow |
| [Attachment Architecture](docs/features/AUTHORIZATION-ATTACHMENTS-ARCHITECTURE.md) | 275/277/RFAI/824 attachment workflows |
| [EDI Workflows](docs/features/EDI-WORKFLOWS-COMPLETE.md) | All X12 transaction processing flows |
| [ADR Index](docs/adr/) | Architecture Decision Records |

## Who This Is For

**Medicaid MCOs** needing CMS-0057-F compliance without a core system upgrade. **Medicare Advantage plans** exiting BPaaS arrangements and building internal operations capability. **Commercial payers** modernizing EDI infrastructure and adding FHIR APIs. **Health plan startups** that need a production-grade payer platform from day one.

## License

Cloud Health Office is licensed under the [Business Source License 1.1](./LICENSE).

| | |
| --- | --- |
| **Free for** | Non-production use — evaluation, development, testing, staging |
| **Requires a license for** | Production use |
| **Converts to** | Apache 2.0 on 2030-03-08 |
| **Licensor** | Aurelianware, Inc |

For commercial licensing: [sales@cloudhealthoffice.com](mailto:sales@cloudhealthoffice.com)

## Links

- **Website:** [cloudhealthoffice.com](https://cloudhealthoffice.com)
- **Portal:** [portal.cloudhealthoffice.com](https://portal.cloudhealthoffice.com)
- **GitHub:** [github.com/aurelianware/cloudhealthoffice](https://github.com/aurelianware/cloudhealthoffice)
- **Sales:** [sales@cloudhealthoffice.com](mailto:sales@cloudhealthoffice.com)
- **Enterprise:** [enterprise@cloudhealthoffice.com](mailto:enterprise@cloudhealthoffice.com)

---

<div align="center">

Built by [Aurelianware, Inc.](https://cloudhealthoffice.com) · 25+ years of payer platform experience

</div>