# CloudHealthOffice Release Documentation

This directory contains comprehensive release documentation for CloudHealthOffice versions.

---

## Current Release

### v6.2.0 — The Shining (October 2026)

Loading a plan's data from its core admin system through a daily CPCDS file drop (members,
coverage, claims, providers and prior authorizations), shadow adjudication, benefit plans from
public data, and Payer-to-Payer `$member-match` passing the Inferno PDex kit (13 of 13). Off by default.

| Document | Description |
|----------|-------------|
| [v6.2.0 Release Notes](./RELEASE_NOTES_v6.2.0.md) | Highlights, upgrading from 6.1, what is off by default |
| [Changelog](../../CHANGELOG.md) | Itemized changes |

---

## Previous Releases

### v6.1.1 — Spartacus, patch (October 2026)

Fixes `Coverage?_include=Coverage:payor`, which returned a 400 in the v6.1.0 tag. No configuration
changes. All four nightly Inferno kits pass, apart from known kit issues.

| Document | Description |
|----------|-------------|
| [v6.1.1 Release Notes](./RELEASE_NOTES_v6.1.1.md) | The fix, and what the v6.1.0 tag also included |
| [Changelog](../../CHANGELOG.md) | Itemized changes |

### v6.1.0 — Spartacus (October 2026)

SMART Backend Services authentication in both directions, the public Provider Directory as
Plan-Net 1.2.0, the rest of Bulk Data `$export` (still off by default), and CAQH CORE real-time
270/271 and 276/277. All four nightly Inferno kits pass, apart from known kit issues. No breaking changes.

| Document | Description |
|----------|-------------|
| [v6.1.0 Release Notes](./RELEASE_NOTES_v6.1.0.md) | Highlights, upgrading from 6.0, what is still open for CMS-0057-F |
| [Changelog](../../CHANGELOG.md) | Itemized changes |

### v6.0.0 — Paths of Glory (October 2026)

MongoDB as the only document store (Cosmos DB removed), claims that pay correctly through one
adjudicator, inpatient pricing from CMS's rules, plan configuration, and CMS-0057-F data from
CHO's own services. Breaking: read the upgrade notes. Does not yet complete the January 2027
CMS-0057-F API requirements.

| Document | Description |
|----------|-------------|
| [v6.0.0 Release Notes](./RELEASE_NOTES_v6.0.0.md) | Highlights, breaking changes, upgrading from 5.x, what is not yet done |
| [Changelog](../../CHANGELOG.md) | Itemized changes |

---

### v5.0.0 — 2001: A Payer Odyssey (October 2026)

Cloud Health Office as the system of record behind its CMS-0057-F APIs, claims lifecycle and X12
trading-partner surface; production SMART/OAuth trust and service-wide token auth.

| Document | Description |
|----------|-------------|
| [v5.0.0 Release Notes](./RELEASE_NOTES_v5.0.0.md) | Highlights, breaking changes, upgrade notes |
| [Changelog](../../CHANGELOG.md) | Itemized changes (includes untagged 4.1.0–4.4.0) |

---

### v4.0.0 — Production Multi-Tenant SaaS (February 11, 2026)

Security hardening, multi-tenant SaaS isolation, and cloud portability across Azure and DigitalOcean.

| Document | Description |
|----------|-------------|
| [v4.0.0 Release](../features/RELEASE-v4.0.0.md) | Release overview |
| [Changelog v4.0.0](../../CHANGELOG.md#400---february-11-2026) | Detailed v4.0.0 changes |
| [GitHub release](https://github.com/aurelianware/cloudhealthoffice/releases/tag/v4.0.0) | Published release notes |

---

### v3.0.0 — The Open Frontier Release (December 2025)

Multi-cloud independence, commercial launch readiness, and Kubernetes-native workflow orchestration.

| Document | Description |
|----------|-------------|
| [v3.0.0 Features Overview](./v3.0.0-features-overview.md) | Comprehensive features matrix and capability overview |
| [v3.0.0 Release Notes](./RELEASE_NOTES_v3.0.0.md) | Detailed release notes with upgrade instructions |

**Key Highlights:**
- 🌐 **Multi-Cloud Deployment**: Deploy on Azure, AWS, GCP, or any Kubernetes cluster
- 🏗️ **Argo Workflows**: Cloud-native EDI processing replacing Azure Logic Apps
- 🛒 **Azure Marketplace Ready**: Managed application and SaaS billing
- 🤖 **AI-Powered Analytics**: ClaimRiskScorer for fraud detection

---

### v2.0.0 — FHIR Frontier Forge (November 2025)

FHIR R4 APIs for CMS-0057-F. CMS-0057-F conformance is in progress — see the [ePA gap matrix](../epa-gap-matrix.md).

| Document | Description |
|----------|-------------|
| [Changelog v2.0.0](../../CHANGELOG.md#200---2025-11-28) | Detailed v2.0.0 changes |
| [What's New](../../WHATS-NEW.md#-v200-features-november-2025) | v2.0.0 feature highlights |

**Key Highlights:**
- 🔄 **FHIR R4 Integration**: Complete X12 → FHIR transformation
- 📊 **Config-to-Workflow Generator**: Zero-code payer onboarding
- 🔐 **Security Hardening**: 9/10 security score
- **CMS-0057-F**: conformance in progress

### v1.0.0 — The Sentinel Has Awakened (November 2025)

First production release of CloudHealthOffice.

| Document | Description |
|----------|-------------|
| [Changelog v1.0.0](../../CHANGELOG.md#100---2025-11-21) | Detailed v1.0.0 changes |

**Key Highlights:**
- 🏥 **Core EDI Processing**: 275, 277, 278, 837, 270/271, 276/277
- 🔒 **HIPAA Compliance**: Production-grade security controls
- ⚡ **Zero-Code Onboarding**: JSON configuration-driven deployment

---

## Migration Guides

| Migration Path | Guide |
|----------------|-------|
| **v5.x → v6.0.0** | [Release Notes - Upgrading from 5.x](./RELEASE_NOTES_v6.0.0.md#upgrading-from-5x) |
| **v2.x → v3.0.0** | [Release Notes - Upgrade Instructions](./RELEASE_NOTES_v3.0.0.md#-upgrade-instructions) |
| **Azure Logic Apps → Argo Workflows** | [Argo Workflows Migration Guide](../ARGO-MIGRATION-GUIDE.md) |
| **Multi-Cloud Deployment** | [Multi-Cloud Deployment Guide](../MULTI-CLOUD-DEPLOYMENT.md) |

---

## Announcements

Release announcements and executive communications.

| Announcement | Date |
|--------------|------|
| [v3.0.0 Announcement](../announcements/v3.0.0-announcement.md) | December 2025 |

---

## Support and Resources

| Resource | Link |
|----------|------|
| **Full Changelog** | [CHANGELOG.md](../../CHANGELOG.md) |
| **What's New** | [WHATS-NEW.md](../../WHATS-NEW.md) |
| **Features Matrix** | [FEATURES.md](../../FEATURES.md) |
| **Quick Start** | [QUICKSTART.md](../../QUICKSTART.md) |
| **GitHub Issues** | [Issues](https://github.com/aurelianware/cloudhealthoffice/issues) |
| **Discussions** | [Discussions](https://github.com/aurelianware/cloudhealthoffice/discussions) |

---

## Version Support Policy

| Version | Release Date | End of Support | Status |
|---------|-------------|----------------|--------|
| v3.0.0 | December 2025 | December 2027 | ✅ Active |
| v2.0.0 | November 2025 | November 2026 | ⚠️ Maintenance |
| v1.0.0 | November 2025 | May 2026 | 🔴 Security only |

---

*BSL 1.1 • CloudHealthOffice*
