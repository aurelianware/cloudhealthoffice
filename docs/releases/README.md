# CloudHealthOffice Release Documentation

This directory contains comprehensive release documentation for CloudHealthOffice versions.

---

## Current Release

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

## Previous Releases

### v2.0.0 — FHIR Frontier Forge (November 2025)

Complete CMS-0057-F compliance with production-ready FHIR R4 APIs.

| Document | Description |
|----------|-------------|
| [Changelog v2.0.0](../../CHANGELOG.md#200---2025-11-28) | Detailed v2.0.0 changes |
| [What's New](../../WHATS-NEW.md#-v200-features-november-2025) | v2.0.0 feature highlights |

**Key Highlights:**
- 🔄 **FHIR R4 Integration**: Complete X12 → FHIR transformation
- 📊 **Config-to-Workflow Generator**: Zero-code payer onboarding
- 🔐 **Security Hardening**: 9/10 security score
- ✅ **CMS-0057-F Ready**: 100% compliance

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
| **v2.x → v3.0.0** | [Release Notes - Upgrade Instructions](./RELEASE_NOTES_v3.0.0.md#-upgrade-instructions) |
| **Azure Logic Apps → Argo Workflows** | [Argo Workflows Migration Guide](../ARGO-MIGRATION-GUIDE.md) |
| **Multi-Cloud Deployment** | [Multi-Cloud Deployment Guide](../MULTI-CLOUD-DEPLOYMENT.md) |
| **Legacy Systems** | [Migration Wizard](../../tools/migration-wizard/README.md) |

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
