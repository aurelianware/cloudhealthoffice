# Changelog

All notable changes to Cloud Health Office will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [1.0.0] - 2025-11-21

### The Sentinel Has Awakened

This is the first production release of Cloud Health Office — the open-source, Azure-native, HIPAA-engineered platform that ends decades of payer EDI pain.

### Added

#### Core Platform
- **Multi-Tenant SaaS Architecture**: Configuration-driven platform supporting unlimited health plans
- **CLI Onboarding Wizard**: Complete deployment from worksheet to production in <45 minutes
- **Zero-Code Payer Onboarding**: Add new payers via JSON configuration without custom development
- **Backend-Agnostic Design**: Works with any claims system (QNXT, FacetsRx, TriZetto, Epic, Cerner, custom)

#### EDI Transaction Processing
- **275 Attachments**: Clinical and administrative attachment processing with file validation
- **277 RFAI**: Request for Additional Information outbound workflow
- **278 Authorizations**: Prior authorization requests (inpatient, outpatient, referrals)
- **278 Authorization Inquiry (X215)**: Real-time status checks for existing authorizations
- **278 Replay Endpoint**: HTTP endpoint for deterministic 278 transaction replay
- **837 Claims**: Professional, Institutional, and Dental claims submission support
- **270/271 Eligibility**: Real-time eligibility verification with 6 search methods
- **276/277 Claim Status**: Claim status inquiries with date range filtering
- **Appeals Processing**: Appeals submission and tracking with 8 sub-statuses
- **ECS (Enhanced Claim Status)**: Advanced claim status with extended data and 4 query methods

#### Clearinghouse Integration
- **Availity Integration**: Native SFTP and API connectivity
- **Change Healthcare Support**: Ready for integration
- **Optum 360 Support**: Ready for integration
- **Inovalon Support**: Ready for integration
- **Direct Payer Endpoints**: Configuration-driven connectivity

#### Security & Compliance
- **Zero-Trust Architecture**: Private-endpoint-only, no public IPs
- **Azure Key Vault Premium**: HSM-backed keys (FIPS 140-2 Level 2)
- **Private Endpoints**: VNet integration for Storage, Service Bus, Key Vault
- **PHI Masking**: DCR-based redaction in Application Insights
- **HIPAA Compliance**: 100% technical safeguards addressed
- **Automated Secret Rotation**: API keys and credentials rotate automatically
- **7-Year Data Retention**: Automated lifecycle management with tier transitions
- **Audit Logging**: 365-day retention in Log Analytics

#### Infrastructure as Code
- **Complete Bicep Templates**: All Azure resources defined in source
- **Logic Apps Standard Workflows**: 15+ production-ready workflows
- **Modular Security Components**: Key Vault, networking, private endpoints, CMK
- **Multi-Environment Support**: DEV/UAT/PROD configurations
- **GitHub Actions Pipelines**: Automated deployment with approval gates

#### Developer Experience
- **Configuration Schema**: JSON Schema Draft-07 with 200+ validation rules
- **TypeScript Interfaces**: Type-safe configuration handling
- **OpenAPI Specifications**: Complete API documentation
- **Example Configurations**: Medicaid MCO and Regional Blues templates
- **Comprehensive Documentation**: 20+ detailed guides

#### QNXT Integration (First in Open Source)
- **Real-Time Correlation APIs**: Link attachments to claims
- **Appeals Registration**: Direct integration with QNXT Appeals API
- **Authorization Processing**: Complete authorization lifecycle management
- **Eligibility Verification**: Member eligibility checks with retry logic
- **Retry Logic**: 4 retries @ 15-second intervals for API calls

#### Monitoring & Observability
- **Application Insights Integration**: Telemetry and distributed tracing
- **PHI-Safe Logging**: Automated masking of sensitive data
- **Custom Metrics**: Authorization decisions, claim status, appeal tracking
- **Health Checks**: Automated verification post-deployment
- **Dead-Letter Queues**: Failed message handling and replay

### Key Highlights

- **Onboarding time reduction**: 6–18 months (legacy) → <1 hour
- **Professional services cost elimination**: $500k–$2M → $0 (bring-your-own-subscription)
- **First production-grade QNXT REST correlation** in open source
- **Complete source code transparency**: No black boxes, fully auditable
- **Azure Marketplace ready**: Prepared for Managed Application publishing

### Platform Specifications

- **Deployment Target**: Azure (Logic Apps Standard, Data Lake Gen2, Service Bus)
- **Runtime**: Logic Apps Standard (WS1+ SKU)
- **Storage**: Azure Data Lake Storage Gen2 with hierarchical namespace
- **Messaging**: Service Bus Standard tier with topics
- **Security**: Premium Key Vault with HSM-backed keys
- **Monitoring**: Application Insights with PHI masking
- **Language**: TypeScript (generator), Bicep (infrastructure), JSON (workflows)

### Documentation

Complete documentation suite includes:
- **CONTRIBUTING.md**: Development workflow and setup
- **ARCHITECTURE.md**: System architecture and data flows
- **DEPLOYMENT.md**: Step-by-step deployment procedures
- **SECURITY.md**: HIPAA compliance and security practices
- **TROUBLESHOOTING.md**: Common issues and solutions
- **BRANDING-GUIDELINES.md**: Sentinel brand identity standards

### Breaking Changes

N/A - First release

### Security

- All dependencies audited and up-to-date
- No known vulnerabilities in production dependencies
- HIPAA compliance validated for all PHI handling paths
- Security hardening guide included in SECURITY.md

### Known Limitations

- Azure-only deployment (AWS/GCP support planned for Q1 2025)
- Integration Account X12 schemas must be manually imported post-deployment
- API connections require manual authentication configuration
- Azure AD Easy Auth configuration required for replay endpoints

### Migration Guide

N/A - First release

### Contributors

Special thanks to all contributors who made this release possible.

### License

Apache License 2.0 - see [LICENSE](LICENSE) file

---

## The Sentinel Has Awakened

The monolith has landed.  
Legacy EDI integration is now optional.

**Just emerged from the void.**

Star ★ the repo if you believe payers deserve better than 1990s technology in 2025.

---

[1.0.0]: https://github.com/aurelianware/cloudhealthoffice/releases/tag/v1.0.0
