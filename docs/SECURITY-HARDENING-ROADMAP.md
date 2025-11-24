# Security Hardening Roadmap

**Document Version:** 1.0  
**Last Updated:** November 23, 2025  
**Planning Horizon:** 12 months (Q4 2025 - Q3 2026)  
**Owner:** Cloud Health Office Security Team

---

## Executive Summary

This roadmap outlines prioritized security enhancements for Cloud Health Office over the next 12 months. Each initiative includes timeline, resource requirements, expected outcomes, and success metrics.

### Current Security Posture: 8.3/10

**Strengths:**
- ✅ Comprehensive HIPAA technical safeguards implemented
- ✅ Private endpoints eliminate public internet exposure
- ✅ HSM-backed encryption keys
- ✅ Comprehensive audit logging with 7-year retention
- ✅ Managed identity eliminates credential risks

**Target Security Posture: 9.5/10**

**Planned Improvements:**
- 🎯 Zero-trust admin access with JIT permissions
- 🎯 Automated security compliance scanning
- 🎯 Third-party penetration testing validation
- 🎯 Advanced threat protection and SIEM
- 🎯 Security awareness and training program

---

## Table of Contents

- [Q4 2025 (November - December)](#q4-2025-november---december)
- [Q1 2026 (January - March)](#q1-2026-january---march)
- [Q2 2026 (April - June)](#q2-2026-april---june)
- [Q3 2026 (July - September)](#q3-2026-july---september)
- [Resource Requirements](#resource-requirements)
- [Success Metrics](#success-metrics)
- [Risk Assessment](#risk-assessment)

---

## Q4 2025 (November - December)

### 1. Implement Azure AD Privileged Identity Management (PIM)

**Priority:** 🔴 Critical  
**Timeline:** 4 weeks (Nov 25 - Dec 20)  
**Effort:** 40 hours  
**Cost:** $0 (included in Azure AD Premium P2 license - $9/user/month)

**Objectives:**
- Eliminate standing privileged access to production systems
- Implement just-in-time (JIT) admin access
- Require approval workflow for sensitive role activations
- Enable comprehensive audit logging of privileged operations

**Implementation Steps:**

**Week 1 (Nov 25-29):**
- [ ] Review current RBAC role assignments
- [ ] Identify all users with permanent privileged roles
- [ ] Design role-to-eligible-role mappings
- [ ] Define activation durations per role (4-8 hours)
- [ ] Identify approvers for each role
- [ ] Create project plan and communicate to stakeholders

**Week 2 (Dec 2-6):**
- [ ] Enable Azure AD PIM for subscription
- [ ] Configure role settings (activation duration, MFA requirement, approval)
- [ ] Create break-glass accounts (2 accounts)
- [ ] Exclude break-glass accounts from Conditional Access policies
- [ ] Store break-glass credentials in physical safe
- [ ] Configure monitoring alerts for break-glass usage

**Week 3 (Dec 9-13):**
- [ ] Pilot with 5 users: Create eligible role assignments
- [ ] Test role activation process
- [ ] Test approval workflow
- [ ] Verify MFA enforcement at activation
- [ ] Gather feedback and adjust configuration
- [ ] Create user documentation (activation guide)

**Week 4 (Dec 16-20):**
- [ ] Convert remaining users to eligible roles (batch approach)
- [ ] Remove permanent role assignments
- [ ] Conduct training session for all users
- [ ] Monitor activations for first week
- [ ] Document lessons learned

**Success Criteria:**
- ✅ Zero permanent privileged role assignments (except break-glass)
- ✅ All privileged actions require JIT activation
- ✅ 100% of activations logged and monitored
- ✅ User satisfaction score >8/10

**Deliverables:**
- PIM configuration documentation
- User activation guide
- Break-glass procedures document
- Training materials and completion records

**Responsible:** Security Team (Lead: CISO)  
**Dependencies:** Azure AD Premium P2 licenses

---

### 2. Create Formal Security Awareness Training Program

**Priority:** 🟡 High  
**Timeline:** 6 weeks (Nov 25 - Jan 10)  
**Effort:** 60 hours  
**Cost:** $5,000 (external training content + platform)

**Objectives:**
- Reduce human error security incidents by 80%
- Ensure 100% compliance with HIPAA workforce security requirements
- Create culture of security awareness
- Meet audit requirement for documented training program

**Implementation Steps:**

**Week 1-2 (Nov 25 - Dec 6):**
- [ ] Research and select training platform (e.g., KnowBe4, SANS Security Awareness)
- [ ] Define training curriculum:
  - HIPAA fundamentals (60 min)
  - PHI handling and protection (45 min)
  - Phishing and social engineering (30 min)
  - Password and MFA best practices (30 min)
  - Incident reporting procedures (30 min)
  - Secure development practices (45 min for technical staff)
- [ ] Customize content for Cloud Health Office specifics
- [ ] Create assessment quiz for each module (minimum 80% pass required)

**Week 3-4 (Dec 9-20):**
- [ ] Pilot training with security team and select staff
- [ ] Gather feedback and refine content
- [ ] Configure training platform
- [ ] Set up automatic enrollment for new hires
- [ ] Configure tracking and reporting

**Week 5-6 (Jan 6-10):**
- [ ] Launch training to all staff
- [ ] Send enrollment invitations
- [ ] Monitor completion rates
- [ ] Provide support for technical issues
- [ ] Collect feedback via survey

**Training Schedule:**
- **Initial:** All employees complete within 30 days of hire
- **Annual Refresher:** Every 12 months
- **Updates:** As needed for new threats or incidents
- **Phishing Simulations:** Monthly

**Success Criteria:**
- ✅ 100% training completion rate
- ✅ Average assessment score >85%
- ✅ Phishing simulation click rate <5%
- ✅ Zero security incidents due to untrained behavior

**Deliverables:**
- Training platform subscription
- Customized training modules
- Assessment quizzes
- Training completion tracking system
- Certificate of completion for each employee

**Responsible:** HR & Security Team (Lead: Compliance Officer)  
**Dependencies:** Training platform procurement

---

### 3. Document Annual Risk Assessment Process

**Priority:** 🟡 High  
**Timeline:** 2 weeks (Dec 2-13)  
**Effort:** 20 hours  
**Cost:** $0

**Objectives:**
- Formalize risk assessment methodology
- Meet HIPAA Security Management Process requirement
- Provide repeatable process for annual reviews
- Create risk register and treatment plans

**Implementation Steps:**

**Week 1 (Dec 2-6):**
- [ ] Review NIST SP 800-30 (Risk Assessment Guide)
- [ ] Review NIST SP 800-66 Rev. 2 (HIPAA Security Rule Toolkit)
- [ ] Define risk assessment scope (systems, data, processes)
- [ ] Define risk rating methodology (likelihood × impact)
- [ ] Create risk register template
- [ ] Define risk treatment options (accept, mitigate, transfer, avoid)

**Week 2 (Dec 9-13):**
- [ ] Conduct initial risk assessment workshop
- [ ] Identify threats and vulnerabilities
- [ ] Assess likelihood and impact for each risk
- [ ] Prioritize risks
- [ ] Document risk treatment plans
- [ ] Create annual risk assessment schedule

**Risk Assessment Methodology:**

```markdown
## Risk Rating Matrix

### Likelihood Scale
1. Rare (1-5% probability)
2. Unlikely (6-20% probability)
3. Possible (21-50% probability)
4. Likely (51-80% probability)
5. Almost Certain (>80% probability)

### Impact Scale
1. Negligible (minimal business impact, no PHI exposure)
2. Minor (limited business impact, minimal PHI exposure)
3. Moderate (noticeable business impact, limited PHI exposure)
4. Major (significant business impact, substantial PHI exposure)
5. Catastrophic (severe business impact, massive PHI breach)

### Risk Score = Likelihood × Impact

| Score | Risk Level | Action Required |
|-------|------------|-----------------|
| 1-4   | Low        | Accept or mitigate when convenient |
| 5-9   | Medium     | Mitigate within 90 days |
| 10-15 | High       | Mitigate within 30 days |
| 16-25 | Critical   | Mitigate immediately (7 days) |
```

**Success Criteria:**
- ✅ Risk assessment methodology documented
- ✅ Initial risk assessment completed
- ✅ Risk register created with all identified risks
- ✅ Risk treatment plans for all medium+ risks

**Deliverables:**
- Risk assessment methodology document
- Risk register (initial)
- Risk treatment plans
- Annual risk assessment schedule

**Responsible:** Compliance Team (Lead: Compliance Officer)  
**Dependencies:** None

---

## Q1 2026 (January - March)

### 4. Engage Third-Party Security Firm for Penetration Testing

**Priority:** 🔴 Critical  
**Timeline:** 10 weeks (Jan 13 - Mar 21)  
**Effort:** 30 hours (internal coordination)  
**Cost:** $30,000

**Objectives:**
- Independent validation of security controls
- Identify vulnerabilities before attackers do
- Meet industry best practice for penetration testing
- Provide evidence for customer security reviews

**Implementation Steps:**

**Week 1-2 (Jan 13-24):**
- [ ] Create RFP for penetration testing services
- [ ] Identify qualified vendors (minimum 3)
- [ ] Send RFP to vendors
- [ ] Collect and evaluate proposals
- [ ] Check references
- [ ] Select vendor

**Week 3 (Jan 27-31):**
- [ ] Negotiate contract
- [ ] Execute NDA and BAA
- [ ] Define scope and rules of engagement
- [ ] Schedule kickoff meeting
- [ ] Provide system documentation to vendor

**Week 4-5 (Feb 3-14) - External Testing:**
- [ ] Vendor conducts external network scanning
- [ ] Vendor attempts to compromise publicly exposed endpoints
- [ ] Vendor tests HTTPS/TLS configurations
- [ ] Vendor attempts authentication bypass
- [ ] Daily status calls with vendor

**Week 6-7 (Feb 17-28) - Internal Testing:**
- [ ] Vendor conducts simulated insider threat testing
- [ ] Vendor attempts privilege escalation
- [ ] Vendor tests for lateral movement capabilities
- [ ] Vendor attempts unauthorized data access
- [ ] Daily status calls with vendor

**Week 8 (Mar 3-7) - Reporting:**
- [ ] Vendor delivers draft report
- [ ] Internal team reviews findings
- [ ] Clarification meeting with vendor
- [ ] Vendor delivers final report

**Week 9-10 (Mar 10-21) - Remediation:**
- [ ] Prioritize findings (critical/high first)
- [ ] Create remediation plans
- [ ] Implement fixes for critical/high findings
- [ ] Request vendor retest
- [ ] Vendor validates remediation
- [ ] Close findings

**Testing Scope:**
- External penetration testing
- Internal penetration testing (simulated insider)
- Web application security testing (replay278 endpoint)
- API security testing (QNXT integration)
- Social engineering testing (phishing simulation)

**Success Criteria:**
- ✅ Zero critical findings remaining after remediation
- ✅ <5 high findings remaining after remediation
- ✅ All critical/high findings remediated within 30 days
- ✅ Retest confirms remediation effectiveness

**Deliverables:**
- Penetration test report (executive summary + technical details)
- Vulnerability findings with severity ratings
- Proof-of-concept exploits (where applicable)
- Remediation recommendations
- Retest results

**Responsible:** Compliance Team (Lead: CISO)  
**Dependencies:** Budget approval

---

### 5. Implement Microsoft Defender for Cloud with Enhanced Security

**Priority:** 🟡 High  
**Timeline:** 4 weeks (Jan 13 - Feb 7)  
**Effort:** 30 hours  
**Cost:** ~$300/month (~$3,600/year) for enhanced features

**Objectives:**
- Continuous security posture assessment
- Automated vulnerability scanning
- Threat detection and response
- Compliance dashboard for HIPAA/SOC 2

**Implementation Steps:**

**Week 1 (Jan 13-17):**
- [ ] Enable Microsoft Defender for Cloud (formerly Azure Security Center)
- [ ] Enable enhanced security features:
  - Defender for Servers (Logic Apps)
  - Defender for Storage
  - Defender for Key Vault
  - Defender for Resource Manager
- [ ] Configure email notifications for alerts
- [ ] Integrate with Azure Monitor / Log Analytics

**Week 2 (Jan 20-24):**
- [ ] Configure security policies:
  - HIPAA compliance policy
  - Azure Security Benchmark
  - Custom policies for Cloud Health Office
- [ ] Configure compliance dashboard
- [ ] Set up automatic remediation for low-risk findings
- [ ] Configure alert suppression rules (false positives)

**Week 3 (Jan 27-31):**
- [ ] Review initial security score and recommendations
- [ ] Prioritize recommendations (quick wins first)
- [ ] Implement high-impact, low-effort improvements
- [ ] Configure workflow automation for common tasks
- [ ] Create custom workbooks for security metrics

**Week 4 (Feb 3-7):**
- [ ] Train security team on Defender for Cloud
- [ ] Create runbooks for common alerts
- [ ] Configure integration with incident management system
- [ ] Document security operations procedures
- [ ] Establish weekly security review meetings

**Defender for Cloud Features:**
- **Security Score:** Quantitative measure of security posture (0-100)
- **Regulatory Compliance:** HIPAA, SOC 2, Azure Security Benchmark dashboards
- **Vulnerability Assessment:** Integrated vulnerability scanning for all resources
- **Threat Protection:** Real-time threat detection with ML-based analytics
- **Just-in-Time VM Access:** Reduce attack surface for management ports (N/A for Logic Apps)
- **Adaptive Application Controls:** Whitelist approved applications (N/A for PaaS)
- **File Integrity Monitoring:** Detect unauthorized changes to critical files
- **Security Alerts:** Centralized view of all security events

**Success Criteria:**
- ✅ Security score >85/100
- ✅ 100% compliance with HIPAA policy
- ✅ Zero critical vulnerabilities unaddressed
- ✅ Mean time to detect (MTTD) <1 hour
- ✅ Mean time to respond (MTTR) <4 hours

**Deliverables:**
- Defender for Cloud configuration documentation
- Security operations runbooks
- Weekly security report template
- Alert response procedures

**Responsible:** Security Team (Lead: Security Engineer)  
**Dependencies:** Budget approval for enhanced features

---

### 6. Implement Automated Compliance Scanning with Azure Policy

**Priority:** 🟡 High  
**Timeline:** 3 weeks (Feb 10 - Feb 28)  
**Effort:** 25 hours  
**Cost:** $0 (included with Azure subscription)

**Objectives:**
- Continuous compliance validation
- Prevent non-compliant resource deployments
- Automated remediation of compliance violations
- Compliance reporting for audits

**Implementation Steps:**

**Week 1 (Feb 10-14):**
- [ ] Review built-in Azure Policy definitions
- [ ] Identify policies for HIPAA compliance:
  - Enforce encryption at rest
  - Enforce encryption in transit (TLS 1.2+)
  - Require private endpoints
  - Deny public network access
  - Require diagnostic logging
  - Require managed identity
- [ ] Customize policies for Cloud Health Office requirements
- [ ] Test policies in DEV environment

**Week 2 (Feb 17-21):**
- [ ] Assign policies to production resource groups
- [ ] Configure policy effects:
  - Audit: Log non-compliance
  - Deny: Prevent non-compliant deployments
  - DeployIfNotExists: Auto-remediate
  - Modify: Auto-configure settings
- [ ] Configure exemptions for valid exceptions
- [ ] Set up compliance reporting dashboard

**Week 3 (Feb 24-28):**
- [ ] Review initial compliance scan results
- [ ] Remediate existing non-compliant resources
- [ ] Test policy enforcement (attempt to deploy non-compliant resource)
- [ ] Configure alerts for policy violations
- [ ] Document policy exemption process

**Key Policies to Implement:**

| Policy | Effect | Purpose |
|--------|--------|---------|
| Storage accounts should disable public network access | Deny | Enforce private endpoints |
| Storage accounts should use customer-managed key for encryption | Audit | CMK usage (optional) |
| Azure Key Vault should have soft delete enabled | Deny | Data recovery |
| Azure Key Vault should have purge protection enabled | Deny | Prevent permanent deletion |
| Logic Apps should use managed identity | Audit | No connection strings |
| Diagnostic logs should be enabled | DeployIfNotExists | Audit logging |
| HTTPS-only should be enabled for web apps | Deny | Encryption in transit |
| Minimum TLS version should be 1.2 | Deny | Strong encryption |

**Success Criteria:**
- ✅ 100% compliance with enforced policies
- ✅ Zero policy exemptions (or fully documented exemptions)
- ✅ Automated remediation for >90% of violations
- ✅ Compliance dashboard shows real-time status

**Deliverables:**
- Azure Policy assignment documentation
- Compliance dashboard
- Policy exemption request process
- Compliance report template

**Responsible:** DevOps Team (Lead: DevOps Engineer)  
**Dependencies:** None

---

## Q2 2026 (April - June)

### 7. Implement Security Information and Event Management (SIEM)

**Priority:** 🟠 Medium  
**Timeline:** 6 weeks (Apr 1 - May 9)  
**Effort:** 60 hours  
**Cost:** ~$2,000/month (~$24,000/year) for Azure Sentinel

**Objectives:**
- Centralized security event monitoring
- Advanced threat detection with AI/ML
- Automated incident response
- Comprehensive security operations center (SOC) capabilities

**Implementation Steps:**

**Week 1-2 (Apr 1-11):**
- [ ] Enable Azure Sentinel (Microsoft's cloud-native SIEM)
- [ ] Connect data sources:
  - Azure Activity Logs
  - Azure AD Sign-in Logs
  - Azure AD Audit Logs
  - Application Insights
  - Key Vault Audit Logs
  - Microsoft Defender for Cloud alerts
- [ ] Configure data retention (365 days for HIPAA)
- [ ] Estimate monthly cost based on data ingestion volume

**Week 3-4 (Apr 14-25):**
- [ ] Enable built-in analytics rules:
  - Failed sign-in attempts
  - Privileged role activations
  - Break-glass account usage
  - Unusual resource deletions
  - Suspicious IP addresses
  - Mass file downloads
- [ ] Create custom analytics rules for Cloud Health Office:
  - PHI access patterns
  - Logic App workflow failures
  - Integration Account configuration changes
  - SFTP connection anomalies
- [ ] Configure alert severity and priority

**Week 5 (Apr 28 - May 2):**
- [ ] Configure automation playbooks (Logic Apps):
  - Auto-disable compromised user accounts
  - Send Teams/email notifications for high-severity incidents
  - Create ServiceNow tickets for incidents
  - Block malicious IP addresses
- [ ] Test automation playbooks
- [ ] Configure user and entity behavior analytics (UEBA)

**Week 6 (May 5-9):**
- [ ] Create security operations workbooks:
  - Security incidents overview
  - User activity monitoring
  - Resource access patterns
  - Threat intelligence
- [ ] Train security team on Sentinel
- [ ] Create incident response procedures
- [ ] Conduct tabletop exercise

**Azure Sentinel Features:**
- **Multi-Cloud:** Supports Azure, AWS, GCP, on-premises
- **AI-Powered:** Machine learning-based threat detection
- **Automation:** Built-in SOAR (Security Orchestration, Automation, and Response)
- **Threat Intelligence:** Integration with Microsoft Threat Intelligence
- **Investigation:** Graph-based investigation tools
- **Hunting:** Advanced query language (KQL) for proactive hunting

**Success Criteria:**
- ✅ All critical data sources connected
- ✅ >20 analytics rules configured and tuned
- ✅ <5% false positive rate
- ✅ Mean time to detect (MTTD) <30 minutes
- ✅ Mean time to respond (MTTR) <2 hours

**Deliverables:**
- Azure Sentinel configuration documentation
- Custom analytics rules
- Automation playbooks
- Security operations workbooks
- Incident response procedures

**Responsible:** Security Team (Lead: Security Engineer)  
**Dependencies:** Budget approval

---

### 8. Conduct Security Awareness Phishing Simulations

**Priority:** 🟠 Medium  
**Timeline:** Ongoing (monthly simulations starting Apr 1)  
**Effort:** 4 hours/month  
**Cost:** $2,000/year (included in training platform)

**Objectives:**
- Measure phishing susceptibility
- Provide real-time training for clicked phishing emails
- Reduce phishing click rate to <5%
- Build security culture

**Implementation:**

**Monthly Simulations:**
- [ ] Configure phishing campaign (vary difficulty, tactics)
- [ ] Target all users (or random sample)
- [ ] Send simulated phishing emails
- [ ] Track click rate, credential entry rate, report rate
- [ ] Provide immediate training to users who clicked
- [ ] Generate monthly report

**Campaign Themes (Rotate):**
- Password expiration notification
- HR policy update
- IT support request
- Shipping notification
- Fake invoice
- Executive impersonation
- Benefits enrollment
- Security alert (ironic but effective)

**Difficulty Levels:**
- **Easy:** Obvious red flags, poor grammar, suspicious sender
- **Medium:** Looks legitimate, requires careful inspection
- **Hard:** Highly sophisticated, nearly identical to real emails

**Success Criteria:**
- ✅ Click rate <5% by end of Q2
- ✅ Credential entry rate <1%
- ✅ Report rate >50%
- ✅ Improved performance month-over-month

**Deliverables:**
- Monthly phishing simulation reports
- Trending analysis
- Remedial training for repeat clickers
- Best practices guidance

**Responsible:** Security Team (Lead: Security Awareness Coordinator)  
**Dependencies:** Training platform with phishing simulation capability

---

### 9. Implement Backup and Disaster Recovery Testing

**Priority:** 🟠 Medium  
**Timeline:** 4 weeks (May 12 - Jun 6)  
**Effort:** 40 hours  
**Cost:** ~$500/month for additional backups and DR environment

**Objectives:**
- Validate backup and recovery capabilities
- Meet HIPAA contingency plan requirements
- Define Recovery Time Objective (RTO) and Recovery Point Objective (RPO)
- Document disaster recovery procedures

**Implementation Steps:**

**Week 1 (May 12-16):**
- [ ] Review current backup configurations:
  - Storage account soft delete (90 days)
  - Blob versioning enabled
  - Geo-redundant storage (GRS)
  - Key Vault soft delete (90 days)
- [ ] Define RTO and RPO targets:
  - RTO: 4 hours (maximum downtime)
  - RPO: 15 minutes (maximum data loss)
- [ ] Document recovery procedures for each component

**Week 2 (May 19-23):**
- [ ] Implement additional backup mechanisms:
  - Daily Infrastructure as Code snapshots (Bicep templates)
  - Daily Logic App workflow exports
  - Weekly Key Vault secret exports (encrypted)
  - Monthly full system configuration export
- [ ] Store backups in secondary region (geo-replication)
- [ ] Configure backup retention (7 years for HIPAA)

**Week 3 (May 27-30):**
- [ ] Create disaster recovery runbook
- [ ] Document step-by-step recovery procedures
- [ ] Identify roles and responsibilities
- [ ] Create decision tree for disaster scenarios
- [ ] Define communication plan

**Week 4 (Jun 2-6):**
- [ ] Conduct disaster recovery exercise:
  - Simulate region outage
  - Execute recovery procedures
  - Measure recovery time
  - Validate data integrity
  - Document lessons learned
- [ ] Update DR runbook based on findings

**Disaster Recovery Scenarios:**

| Scenario | Probability | Impact | RTO | RPO |
|----------|-------------|--------|-----|-----|
| Azure region outage | Low | High | 4 hours | 15 minutes |
| Storage account deletion | Very Low | High | 2 hours | 0 (soft delete recovery) |
| Logic App misconfiguration | Medium | Medium | 1 hour | 0 (IaC redeployment) |
| Key Vault deletion | Very Low | Critical | 2 hours | 0 (soft delete recovery) |
| Ransomware attack | Low | Critical | 4 hours | 15 minutes |

**Success Criteria:**
- ✅ Recovery procedures documented for all scenarios
- ✅ DR exercise completed successfully
- ✅ RTO met: Full recovery in <4 hours
- ✅ RPO met: Data loss <15 minutes
- ✅ Quarterly DR testing scheduled

**Deliverables:**
- Backup configuration documentation
- Disaster recovery runbook
- DR exercise results
- Quarterly DR test schedule

**Responsible:** DevOps Team (Lead: DevOps Engineer)  
**Dependencies:** Secondary region resources

---

## Q3 2026 (July - September)

### 10. Achieve SOC 2 Type II Certification (6-Month Observation)

**Priority:** 🟠 Medium (Long-term strategic)  
**Timeline:** 6 months (Apr 1 - Sep 30) + 3 months audit  
**Effort:** 150 hours (internal) + auditor time  
**Cost:** $50,000 (audit) + $10,000 (consulting)

**Objectives:**
- Independent validation of security controls
- Meet customer security requirements
- Competitive differentiation
- Demonstrate operational maturity

**Observation Period:** Apr 1 - Sep 30 (6 months minimum)

**Implementation Steps:**

**Pre-Observation (Jan - Mar):**
- [ ] Engage SOC 2 consultant to assess readiness
- [ ] Identify gaps in current controls
- [ ] Remediate gaps before observation period
- [ ] Select SOC 2 auditor (Big Four or specialized firm)
- [ ] Execute engagement letter

**Observation Period (Apr - Sep):**
- [ ] Maintain consistent security controls
- [ ] Collect evidence of control operation:
  - Access reviews (quarterly)
  - Security training completion
  - Incident response logs
  - Vulnerability scanning results
  - Penetration test results
  - Change management logs
  - Monitoring and alerting evidence
- [ ] Conduct internal audits (monthly)
- [ ] Monthly calls with auditor

**Audit Period (Oct - Dec):**
- [ ] Auditor tests controls over observation period
- [ ] Respond to auditor requests for evidence
- [ ] Clarification meetings as needed
- [ ] Receive draft report
- [ ] Provide management responses to any exceptions
- [ ] Receive final SOC 2 Type II report

**Trust Services Criteria:**

| Criterion | Focus Area | Current Status |
|-----------|------------|----------------|
| **CC1: Control Environment** | Governance, integrity, ethical values | ✅ Strong |
| **CC2: Communication** | Internal/external communication | ✅ Strong |
| **CC3: Risk Assessment** | Risk identification and mitigation | 🟡 Good (needs formalization) |
| **CC4: Monitoring** | Performance monitoring | ✅ Strong |
| **CC5: Control Activities** | Policies and procedures | ✅ Strong |
| **CC6: Logical Access** | Access control, MFA | ✅ Strong |
| **CC7: System Operations** | Backups, DR, capacity | 🟡 Good (DR testing needed) |
| **CC8: Change Management** | Change control procedures | ✅ Strong (GitHub PRs) |
| **CC9: Risk Mitigation** | Threat protection | ✅ Strong |
| **A1: Availability** | System availability, SLA | ✅ Strong (99.9% Azure SLA) |

**Success Criteria:**
- ✅ SOC 2 Type II report received
- ✅ Clean opinion (no qualified opinion)
- ✅ Zero control exceptions
- ✅ All gaps remediated before observation period

**Deliverables:**
- SOC 2 Type II report
- Control evidence documentation
- Management response (if exceptions)
- Customer-facing summary report

**Responsible:** Compliance Team (Lead: Compliance Officer)  
**Dependencies:** Budget approval, 6-month observation period

---

### 11. Implement Web Application Firewall (WAF) for HTTP Endpoints

**Priority:** 🟢 Low (Enhancement)  
**Timeline:** 3 weeks (Jul 7 - Jul 25)  
**Effort:** 25 hours  
**Cost:** ~$200/month (~$2,400/year)

**Objectives:**
- Protect replay278 HTTP endpoint from OWASP Top 10 attacks
- Block malicious traffic before it reaches Logic App
- Provide DDoS protection
- Centralized security policy management

**Implementation Steps:**

**Week 1 (Jul 7-11):**
- [ ] Deploy Azure Application Gateway with WAF
- [ ] Configure backend pool (Logic App endpoint)
- [ ] Configure HTTP/HTTPS listeners
- [ ] Configure routing rules
- [ ] Test connectivity

**Week 2 (Jul 14-18):**
- [ ] Enable WAF with OWASP Core Rule Set (CRS) 3.2
- [ ] Configure WAF mode (Detection initially, then Prevention)
- [ ] Configure exclusions for false positives
- [ ] Enable bot protection
- [ ] Enable rate limiting (100 req/min per IP)

**Week 3 (Jul 21-25):**
- [ ] Test WAF blocking capabilities:
  - SQL injection attempts
  - Cross-site scripting (XSS)
  - Path traversal
  - Command injection
  - Malformed requests
- [ ] Review WAF logs and tune rules
- [ ] Switch to Prevention mode
- [ ] Configure alerts for blocked requests

**WAF Rule Categories:**
- **SQL Injection:** Blocks SQL injection attempts
- **XSS:** Blocks cross-site scripting attacks
- **LFI/RFI:** Blocks local/remote file inclusion
- **RCE:** Blocks remote code execution attempts
- **Protocol Attacks:** Blocks HTTP protocol violations
- **Suspicious User-Agents:** Blocks known malicious bots
- **Rate Limiting:** Prevents DoS attacks

**Success Criteria:**
- ✅ WAF deployed and operational
- ✅ OWASP Top 10 attacks blocked
- ✅ <5% false positive rate
- ✅ Zero performance degradation

**Deliverables:**
- Application Gateway + WAF configuration documentation
- WAF rule tuning guide
- Alert response procedures
- Performance baseline

**Responsible:** DevOps Team (Lead: DevOps Engineer)  
**Dependencies:** Budget approval

---

### 12. Implement Advanced Threat Protection (ATP)

**Priority:** 🟢 Low (Enhancement)  
**Timeline:** 2 weeks (Aug 4-15)  
**Effort:** 20 hours  
**Cost:** ~$15/user/month (~$2,700/year for 15 users)

**Objectives:**
- Protect against phishing, malware, and ransomware in email
- Safe links and safe attachments for Office 365
- Anti-phishing protection
- Automated investigation and response

**Implementation Steps:**

**Week 1 (Aug 4-8):**
- [ ] Enable Microsoft Defender for Office 365 Plan 2
- [ ] Configure anti-phishing policies:
  - Impersonation protection (protect executives)
  - Mailbox intelligence
  - Spoof intelligence
- [ ] Configure safe attachments:
  - Dynamic delivery (scan attachments before delivery)
  - Block malicious attachments
- [ ] Configure safe links:
  - Rewrite URLs in emails
  - Check URLs at click time

**Week 2 (Aug 11-15):**
- [ ] Enable attack simulation training
- [ ] Configure automated investigation and response (AIR)
- [ ] Review initial findings and tune policies
- [ ] Configure alerts and notifications
- [ ] Train users on quarantine notifications

**ATP Features:**
- **Safe Attachments:** Detonates attachments in sandbox environment
- **Safe Links:** Rewrites URLs and scans at click time
- **Anti-Phishing:** Detects and blocks impersonation attempts
- **Attack Simulation:** Built-in phishing simulation platform
- **Automated Investigation:** AI-powered threat investigation
- **Threat Explorer:** Advanced threat hunting and analysis

**Success Criteria:**
- ✅ ATP policies configured and enforced
- ✅ Zero malware delivered via email
- ✅ Phishing emails automatically quarantined
- ✅ User awareness improved (via attack simulation)

**Deliverables:**
- ATP configuration documentation
- Policy tuning guide
- User guidance on quarantine
- Monthly threat report

**Responsible:** IT Team (Lead: IT Manager)  
**Dependencies:** Microsoft 365 E5 or Defender for Office 365 Plan 2 license

---

## Resource Requirements

### Personnel

| Role | Time Allocation | Duration | Total Hours |
|------|----------------|----------|-------------|
| **CISO** | 25% FTE | 12 months | 520 hours |
| **Security Engineer** | 50% FTE | 12 months | 1,040 hours |
| **Compliance Officer** | 25% FTE | 12 months | 520 hours |
| **DevOps Engineer** | 25% FTE | 12 months | 520 hours |
| **IT Manager** | 10% FTE | 12 months | 208 hours |
| **Total** | - | - | **2,808 hours** |

### Budget

| Category | Q4 2025 | Q1 2026 | Q2 2026 | Q3 2026 | Total |
|----------|---------|---------|---------|---------|-------|
| **Licensing** | $180 | $180 | $1,080 | $1,080 | $2,520 |
| **Training Platform** | $2,500 | $1,250 | $1,250 | $1,250 | $6,250 |
| **Penetration Testing** | $0 | $30,000 | $0 | $0 | $30,000 |
| **SIEM (Azure Sentinel)** | $0 | $0 | $6,000 | $6,000 | $12,000 |
| **SOC 2 Audit** | $0 | $0 | $0 | $50,000 | $50,000 |
| **WAF (App Gateway)** | $0 | $0 | $0 | $600 | $600 |
| **ATP (Defender for O365)** | $0 | $0 | $0 | $1,350 | $1,350 |
| **Backup & DR** | $0 | $0 | $1,500 | $1,500 | $3,000 |
| **Consulting** | $0 | $0 | $0 | $10,000 | $10,000 |
| **Contingency (10%)** | $268 | $3,143 | $977 | $7,178 | $11,572 |
| **Total** | **$2,948** | **$34,573** | **$10,747** | **$78,958** | **$127,232** |

**Annual Budget:** $127,232

**Budget Breakdown by Category:**
- **Third-Party Audits/Testing:** $80,000 (63%)
- **Tools and Platforms:** $24,520 (19%)
- **Training and Awareness:** $6,250 (5%)
- **Infrastructure Enhancements:** $5,100 (4%)
- **Contingency:** $11,572 (9%)

---

## Success Metrics

### Security Posture Improvement

| Metric | Baseline | Q4 2025 | Q1 2026 | Q2 2026 | Q3 2026 | Target |
|--------|----------|---------|---------|---------|---------|--------|
| **Overall Security Score** | 8.3/10 | 8.5/10 | 9.0/10 | 9.3/10 | 9.5/10 | 9.5/10 |
| **Permanent Admin Access** | 8 users | 0 users | 0 users | 0 users | 0 users | 0 users |
| **Critical Vulnerabilities** | 0 | 0 | 0 | 0 | 0 | 0 |
| **High Vulnerabilities** | 2 | 1 | 0 | 0 | 0 | 0 |
| **Training Completion** | 60% | 100% | 100% | 100% | 100% | 100% |
| **Phishing Click Rate** | 25% | 15% | 8% | 5% | <5% | <5% |
| **Mean Time to Detect (MTTD)** | 4 hours | 2 hours | 1 hour | 30 min | 15 min | <30 min |
| **Mean Time to Respond (MTTR)** | 12 hours | 8 hours | 4 hours | 2 hours | 1 hour | <2 hours |
| **Security Incidents** | 0/month | 0/month | 0/month | 0/month | 0/month | 0/month |

### Compliance Metrics

| Metric | Baseline | Q4 2025 | Q1 2026 | Q2 2026 | Q3 2026 | Target |
|--------|----------|---------|---------|---------|---------|--------|
| **HIPAA Compliance** | 100% | 100% | 100% | 100% | 100% | 100% |
| **Azure Policy Compliance** | 95% | 98% | 100% | 100% | 100% | 100% |
| **Defender for Cloud Score** | N/A | N/A | 85/100 | 90/100 | 95/100 | >85/100 |
| **Penetration Test Findings (Critical)** | N/A | N/A | 0 | 0 | 0 | 0 |
| **Audit Findings (High/Critical)** | 0 | 0 | 0 | 0 | 0 | 0 |
| **SOC 2 Readiness** | 75% | 80% | 85% | 95% | 100% | 100% |

### Operational Metrics

| Metric | Baseline | Q4 2025 | Q1 2026 | Q2 2026 | Q3 2026 | Target |
|--------|----------|---------|---------|---------|---------|--------|
| **Security Budget Utilization** | 0% | 15% | 50% | 75% | 100% | 100% |
| **Remediation Time (Critical)** | N/A | 5 days | 3 days | 2 days | 1 day | <7 days |
| **Remediation Time (High)** | 20 days | 15 days | 10 days | 7 days | 5 days | <30 days |
| **Security Team Training Hours** | 20 hrs | 40 hrs | 60 hrs | 80 hrs | 100 hrs | 100 hrs/year |
| **DR Exercise Success Rate** | N/A | N/A | N/A | 100% | 100% | 100% |

---

## Risk Assessment

### Project Risks

| Risk | Probability | Impact | Mitigation |
|------|-------------|--------|------------|
| **Budget not approved** | Medium | High | Prioritize critical items; phase implementation |
| **Resource constraints** | Medium | Medium | Engage contractors for specialized tasks |
| **Vendor delays** | Low | Medium | Select vendors with proven track record; build buffer time |
| **Technology changes** | Low | Low | Regular review and adjustment of roadmap |
| **Competing priorities** | Medium | Medium | Executive sponsorship; clear communication |
| **Skills gap** | Medium | Medium | Training and certification for team; contractor support |
| **User resistance (PIM)** | Medium | Medium | Comprehensive training; clear communication of benefits |
| **False positives (WAF, ATP)** | High | Low | Careful tuning; pilot before production |

### Mitigation Strategies

**Budget Constraints:**
- Prioritize initiatives by risk reduction impact
- Implement in phases (critical first)
- Leverage included Azure features before purchasing add-ons
- Seek grants or subsidies for security improvements

**Resource Constraints:**
- Allocate dedicated time for security team
- Hire contractors for penetration testing, SOC 2 audit
- Use managed services (SIEM, training platform)
- Automate repetitive tasks

**Timeline Slippage:**
- Build 20% buffer into all timelines
- Weekly progress reviews
- Early identification of blockers
- Escalation path for critical issues

**Organizational Change:**
- Executive sponsorship and communication
- User training and documentation
- Phased rollout with pilot groups
- Feedback loops and continuous improvement

---

## Conclusion

This roadmap provides a structured approach to enhancing Cloud Health Office's security posture from **8.3/10** to **9.5/10** over 12 months. By implementing just-in-time admin access, automated compliance scanning, third-party validation, and advanced threat protection, Cloud Health Office will:

✅ **Exceed HIPAA requirements** - Going beyond minimum compliance  
✅ **Build customer trust** - SOC 2 Type II certification  
✅ **Reduce security risks** - Proactive threat detection and response  
✅ **Improve operational efficiency** - Automated security operations  
✅ **Demonstrate security maturity** - Industry-leading security practices

**Next Steps:**
1. Review and approve roadmap with executive team
2. Allocate budget for Q4 2025 and Q1 2026 initiatives
3. Assign roles and responsibilities
4. Kick off PIM implementation (Nov 25, 2025)
5. Begin weekly progress reviews

---

**Document Owner:** Cloud Health Office Security Team  
**Approved By:** [CISO Signature] ___________________________  
**Date:** November 23, 2025  
**Next Review:** Quarterly (Feb 23, May 23, Aug 23, 2026)
