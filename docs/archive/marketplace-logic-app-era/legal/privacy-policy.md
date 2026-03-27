# Cloud Health Office Privacy Policy

**Effective Date**: March 1, 2026  
**Last Updated**: February 17, 2026  
**Version**: 2.0

---

## 1. Introduction

This Privacy Policy describes how Aurelianware and its Cloud Health Office platform ("we," "us," or "our") collect, use, disclose, and protect information when you use our HIPAA-compliant EDI integration services ("Services").

Cloud Health Office is designed specifically for healthcare organizations and processes Protected Health Information (PHI) in accordance with the Health Insurance Portability and Accountability Act of 1996 (HIPAA) and its implementing regulations.

## 2. Information We Collect

### 2.1 Customer Account Information

When you create an account or purchase our Services, we collect:

- Organization name and contact information
- Administrator names and email addresses
- Billing information and payment details
- Azure subscription and tenant information
- Technical configuration preferences

### 2.2 Protected Health Information (PHI)

As a Business Associate under HIPAA, we process PHI on behalf of our Covered Entity customers, including:

- Patient/Member identifiers (name, date of birth, member ID)
- Provider information (NPI, names, addresses)
- Clinical information (diagnosis codes, procedure codes, dates of service)
- Claim and prior authorization data
- Attachment contents (medical records, supporting documentation)

### 2.3 Usage Data

We automatically collect certain information about how you interact with our Services:

- API call logs (without PHI content)
- Feature usage patterns
- Performance and error metrics
- Session duration and frequency

### 2.4 Technical Data

We collect technical information necessary for service delivery:

- IP addresses (for security and access control)
- Browser type and version
- Device information
- Azure resource identifiers

## 3. How We Use Information

### 3.1 Service Delivery

We use collected information to:

- Provision and operate the Cloud Health Office platform
- Process EDI transactions (X12 275, 277, 278, 837)
- Provide FHIR R4 API services
- Maintain and improve service performance
- Provide customer support

### 3.2 HIPAA-Regulated Uses

PHI is processed solely for:

- Treatment, Payment, and Healthcare Operations (TPO) activities as directed by Covered Entities
- Compliance with legal and regulatory requirements
- Purposes authorized by the applicable Business Associate Agreement (BAA)

### 3.3 Analytics and Improvement

We use aggregated, de-identified data to:

- Analyze usage patterns to improve our Services
- Develop new features and capabilities
- Benchmark performance metrics
- Conduct research and development

### 3.4 Communications

We may use your contact information to:

- Send service-related notifications and alerts
- Provide product updates and release notes
- Respond to support requests
- Send marketing communications (with consent)

## 4. Information Sharing and Disclosure

### 4.1 We Do Not Sell PHI

We do not sell, rent, or trade Protected Health Information under any circumstances.

### 4.2 Service Providers and Subcontractors

We may share information with trusted service providers who assist in operating our Services:

- **Microsoft Azure**: Cloud infrastructure hosting (BAA in place)
- **Support Systems**: Customer support and ticketing platforms
- **Analytics**: De-identified usage analytics

All subcontractors processing PHI are bound by Business Associate Agreements.

### 4.3 Legal Requirements

We may disclose information when required by law, including:

- HIPAA-permitted disclosures (e.g., public health, judicial proceedings)
- Government audit or investigation requests
- Court orders or subpoenas

### 4.4 Business Transfers

In the event of a merger, acquisition, or asset sale, customer information may be transferred to the acquiring entity, subject to continued compliance with this Privacy Policy and applicable BAAs.

## 5. HIPAA Compliance

### 5.1 Business Associate Agreement

Before processing any PHI, we execute a Business Associate Agreement (BAA) with each Covered Entity customer that specifies:

- Permitted uses and disclosures of PHI
- Safeguards for PHI protection
- Breach notification requirements
- Subcontractor requirements
- Termination and data return/destruction procedures

### 5.2 Administrative Safeguards

We implement administrative safeguards including:

- Designated HIPAA Privacy and Security Officers
- Workforce training on HIPAA requirements
- Policies and procedures for PHI handling
- Sanctions for policy violations

### 5.3 Physical Safeguards

PHI is protected by physical safeguards including:

- Azure datacenter security (SOC 2 Type II certified)
- Facility access controls
- Workstation security policies
- Device and media controls

### 5.4 Technical Safeguards

We implement technical safeguards including:

- Encryption at rest (AES-256) and in transit (TLS 1.2+)
- Access controls and authentication
- Audit logging and monitoring
- Automatic session termination
- Emergency access procedures

### 5.5 Breach Notification

In the event of a breach of unsecured PHI:

- We notify affected Covered Entities within 24 hours of discovery
- We cooperate in breach investigation and mitigation
- We maintain breach documentation for 6 years
- We report to HHS as required by the Breach Notification Rule

## 6. Data Retention

### 6.1 PHI Retention

PHI is retained in accordance with:

- Customer data retention policies
- Applicable BAA requirements
- HIPAA minimum 6-year retention for compliance documentation
- **7-year retention for claims data and EDI transaction archives** (configurable from 1 to 10 years)

### 6.2 Account Information

Customer account information is retained:

- During active subscription
- For 7 years after termination (for compliance and audit records)
- As required by applicable law

### 6.3 Application and Audit Logs

System logs are retained as follows:

- **Application logs**: 365 days (includes performance metrics, error logs, and operational data)
- **Audit logs**: 7 years (includes access logs, PHI disclosure tracking, and security events)
- **Log sanitization**: Control characters (CR/LF) are stripped from log entries to mitigate log forging attacks; PHI redaction is applied within designated PHI-aware logging components

### 6.4 Data Deletion

Upon termination of services:

- PHI is securely deleted within **90 days** (or returned per BAA within 60 days if requested)
- Backups are purged according to retention schedules (maximum 90 days)
- Audit logs are retained for 7 years for compliance purposes
- De-identified, aggregated data may be retained indefinitely for analytics and service improvement

## 7. Data Security

### 7.1 Security Measures

We implement comprehensive security measures including:

- Microsoft Azure SOC 2 Type II certified infrastructure
- Premium Key Vault with HSM-backed encryption keys
- Private endpoints for network isolation
- Role-Based Access Control (RBAC)
- Multi-factor authentication
- Regular security assessments and penetration testing

### 7.2 Security Certifications

Our infrastructure maintains:

- HIPAA compliance attestation
- SOC 2 Type II certification (via Azure)
- ISO 27001 certification (via Azure)

### 7.3 Incident Response

We maintain an incident response program including:

- 24/7 security monitoring
- Incident classification and escalation procedures
- Forensic investigation capabilities
- Communication protocols for customers
- Post-incident analysis and remediation

## 8. Your Rights

### 8.1 HIPAA Individual Rights

As a Business Associate, we support Covered Entities in fulfilling individual rights including:

- Right to access PHI
- Right to request amendment of PHI
- Right to an accounting of disclosures
- Right to request restrictions
- Right to confidential communications

Individuals should contact their Covered Entity (health plan/provider) to exercise these rights.

### 8.2 Customer Rights

As a customer, you have the right to:

- Access your account information
- Update or correct your information
- Request data export in standard formats
- Close your account (subject to retention requirements)
- Opt out of marketing communications

### 8.3 California Privacy Rights (CCPA/CPRA)

California residents have additional rights under the California Consumer Privacy Act (CCPA) and California Privacy Rights Act (CPRA):

- **Right to Know**: Right to know what personal information is collected, used, shared, and sold
- **Right to Delete**: Right to delete personal information (subject to legal and contractual exceptions)
- **Right to Opt-Out**: Right to opt out of sale or sharing of personal information (**we do not sell or share personal information**)
- **Right to Correct**: Right to correct inaccurate personal information
- **Right to Limit Use**: Right to limit use and disclosure of sensitive personal information (including PHI)
- **Right to Non-Discrimination**: Right not to receive discriminatory treatment for exercising CCPA/CPRA rights

**How to Exercise Rights**: Email privacy@cloudhealthoffice.com with subject "CCPA/CPRA Request"

**Response Time**: We respond to verified requests within 45 days (may be extended by 45 days if necessary)

## 9. Children's Privacy

Our Services are not directed to individuals under 18 years of age. We do not knowingly collect personal information from children. PHI of minors is processed solely as directed by Covered Entities in accordance with applicable law.

## 10. International Data Transfers and GDPR

### 10.1 Data Location

By default, customer data is processed and stored in the Azure region selected during deployment (United States regions). Data does not leave the selected geographic region unless explicitly configured by the customer.

### 10.2 European Union and GDPR

If Customer operates in the European Union or processes data of EU residents:

**Legal Basis for Processing**:
- **Contractual necessity**: Processing necessary to perform our contractual obligations
- **Legitimate interests**: Processing necessary for our legitimate business interests (e.g., fraud prevention, security)
- **Legal compliance**: Processing necessary to comply with legal obligations (e.g., HIPAA, tax laws)
- **Consent**: Where explicitly obtained for specific purposes (e.g., marketing communications)

**EU Resident Rights** (in addition to HIPAA rights):
- Right of access to personal data
- Right to rectification of inaccurate data
- Right to erasure ("right to be forgotten") - subject to legal exceptions
- Right to restriction of processing
- Right to data portability
- Right to object to processing
- Right not to be subject to automated decision-making (including profiling)

**Data Protection Officer**: For GDPR inquiries, contact dpo@cloudhealthoffice.com

### 10.3 Cross-Border Transfers

If data transfer outside the United States is necessary:

- We utilize **Standard Contractual Clauses (SCCs)** approved by the European Commission
- We implement additional safeguards as required by the Schrems II decision
- We ensure receiving parties provide adequate protection under GDPR standards
- We comply with applicable data localization requirements

### 10.4 UK GDPR

For UK-based customers, we comply with the UK General Data Protection Regulation (UK GDPR) and Data Protection Act 2018. The same rights and protections apply as described in Section 10.2.

## 11. Cookies and Tracking Technologies

### 11.1 Use of Cookies

The Cloud Health Office website and portal use cookies and similar tracking technologies for the following purposes:

**Essential Cookies** (always active):
- Authentication and session management
- Security and fraud prevention
- Load balancing and performance optimization

**Analytics Cookies** (opt-in):
- Website usage analytics (Google Analytics)
- Feature usage tracking (Application Insights)
- Performance monitoring

**Marketing Cookies** (opt-in):
- LinkedIn Insight Tag (for ad targeting)
- Campaign attribution

### 11.2 Cookie Management

You can control cookie preferences through:
- Browser settings (most browsers allow blocking cookies)
- Our cookie consent banner (first-time visitors)
- Cookie preference center: https://cloudhealthoffice.com/cookie-settings

**Note**: Disabling essential cookies may impair functionality of the Services.

### 11.3 Do Not Track (DNT)

We honor Do Not Track (DNT) browser signals for analytics and marketing cookies, but essential cookies remain active to ensure service functionality.

---

## 12. Changes to This Policy

We may update this Privacy Policy from time to time. Changes will be:

- Posted on our website at https://cloudhealthoffice.com/legal/privacy-policy with the updated effective date
- Communicated to customers via email for **material changes** at least 30 days before the effective date
- Effective 30 days after posting (or as otherwise specified)

**Material changes** include changes to data retention periods, new uses of PHI, or changes to your rights.

Your continued use of the Services after changes become effective constitutes acceptance of the updated Policy. If you do not agree to the updated Policy, you may terminate your subscription as provided in the Terms of Service.

## 13. Contact Information

### Privacy Inquiries

**Cloud Health Office Privacy Office**  
Email: privacy@cloudhealthoffice.com  
Address: [Company Address]

### HIPAA Compliance

**HIPAA Privacy Officer**  
Email: hipaa-privacy@cloudhealthoffice.com

**HIPAA Security Officer**  
Email: hipaa-security@cloudhealthoffice.com

### Data Protection Officer (for GDPR inquiries)

Email: dpo@cloudhealthoffice.com

### General Support

Email: support@cloudhealthoffice.com  
Website: https://support.cloudhealthoffice.com

---

## Appendix A: Summary of PHI Handling

| Category | Data Elements | Use | Retention |
|----------|--------------|-----|-----------|
| Member Data | Name, DOB, Member ID | EDI processing | Per BAA |
| Provider Data | NPI, Name, Address | EDI processing | Per BAA |
| Clinical Data | Dx/CPT codes, DOS | EDI processing | Per BAA |
| Claim Data | Claim #, Status | EDI processing | 7 years |
| Attachments | Medical records | 275 processing | 7 years |

## Appendix B: Third-Party Service Providers (Subprocessors)

| Provider | Service | BAA | Certification | Data Location |
|----------|---------|-----|---------------|---------------|
| Microsoft Azure | Cloud Infrastructure | Yes | SOC 2, ISO 27001, HIPAA | United States |
| Application Insights | Monitoring | Yes | SOC 2 (via Azure) | United States |
| Service Bus | Messaging | Yes | SOC 2 (via Azure) | United States |
| Stripe, Inc. | Payment Processing | No | PCI DSS Level 1 | United States |
| SendGrid (Twilio) | Transactional Email | Yes | SOC 2 Type II | United States |

**Subprocessor Updates**: We will notify customers of any additions or changes to subprocessors with at least **30 days' advance notice**. Customers may object to new subprocessors within 15 days by contacting privacy@cloudhealthoffice.com.

---

**Cloud Health Office** – Advancing Healthcare EDI Integration  
**Source-Available (BSL 1.1) | Azure-Native | HIPAA-Compliant**

© 2025 Aurelianware. All rights reserved.
