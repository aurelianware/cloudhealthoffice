# Cloud Health Office Service Level Agreement (SLA)

**Effective Date**: December 1, 2024  
**Last Updated**: December 1, 2024  
**Version**: 1.0

---

## 1. Overview

This Service Level Agreement ("SLA") describes the service commitments for the Cloud Health Office platform provided by Aurelianware ("Provider"). This SLA applies to all customers with an active subscription.

This SLA is incorporated by reference into your Master Services Agreement or applicable subscription terms.

## 2. Definitions

**"Availability"** means the percentage of time the Services are operational and accessible during a calendar month.

**"Downtime"** means any period during which the Services are unavailable, excluding Scheduled Maintenance and Exclusions.

**"Monthly Uptime Percentage"** means the total number of minutes in a calendar month minus the number of minutes of Downtime, divided by the total number of minutes in that calendar month.

**"Scheduled Maintenance"** means planned maintenance activities for which Provider gives at least 7 days' advance notice.

**"Emergency Maintenance"** means unplanned maintenance necessary to address critical security vulnerabilities or system stability issues.

**"Service Credits"** means credits applied to your account as compensation for Downtime below committed SLA levels.

## 3. Service Level Commitments

### 3.1 Availability by Plan

| Plan | Monthly Uptime Commitment | Maximum Allowed Downtime |
|------|--------------------------|-------------------------|
| Starter | 99.5% | 3 hours 39 minutes |
| Professional | 99.9% | 43 minutes |
| Enterprise | 99.95% | 22 minutes |

### 3.2 Included Services

The availability commitments apply to the following Cloud Health Office components:

- **Logic App Workflows**: EDI processing workflows (275, 277, 278, 837)
- **FHIR R4 APIs**: Patient Access, Provider Access, Payer-to-Payer endpoints
- **Integration Account**: X12 encoding/decoding services
- **Service Bus**: Message queue and topic operations
- **Management Portal**: Customer configuration and monitoring interface

### 3.3 Performance Targets

| Metric | Target | Measurement |
|--------|--------|-------------|
| API Response Time (P95) | < 2 seconds | 95th percentile |
| Workflow Processing Time | < 30 seconds | Average per transaction |
| FHIR API Response Time (P95) | < 1 second | 95th percentile |
| Bulk Export Initiation | < 5 seconds | Time to start export |

### 3.4 Transaction Processing

| Transaction Type | Processing SLA | Acknowledgment |
|-----------------|----------------|----------------|
| 837 Claims | < 60 seconds | Immediate |
| 278 Prior Auth | < 30 seconds | Immediate |
| 275 Attachments | < 120 seconds | Immediate |
| FHIR Queries | < 2 seconds | Synchronous |

## 4. Service Credits

### 4.1 Credit Schedule

If Provider fails to meet the Monthly Uptime Commitment, Customer is entitled to Service Credits as follows:

**Starter Plan:**

| Monthly Uptime | Service Credit |
|----------------|----------------|
| 99.0% - 99.4% | 10% of monthly fee |
| 95.0% - 98.9% | 25% of monthly fee |
| Below 95.0% | 50% of monthly fee |

**Professional Plan:**

| Monthly Uptime | Service Credit |
|----------------|----------------|
| 99.5% - 99.8% | 10% of monthly fee |
| 99.0% - 99.4% | 25% of monthly fee |
| 95.0% - 98.9% | 50% of monthly fee |
| Below 95.0% | 100% of monthly fee |

**Enterprise Plan:**

| Monthly Uptime | Service Credit |
|----------------|----------------|
| 99.9% - 99.94% | 10% of monthly fee |
| 99.5% - 99.8% | 25% of monthly fee |
| 99.0% - 99.4% | 50% of monthly fee |
| Below 99.0% | 100% of monthly fee |

### 4.2 Credit Request Process

To receive Service Credits:

1. Submit a request via support portal within 30 days of the incident month
2. Include: Customer name, affected dates/times, description of impact
3. Provider will review and respond within 10 business days
4. Approved credits will be applied to the next billing cycle

### 4.3 Credit Limitations

- Service Credits are the sole and exclusive remedy for Downtime
- Maximum credits per month: 100% of monthly fee for that service
- Credits may not be exchanged for cash
- Credits expire 12 months after issuance
- Credits do not apply to Azure infrastructure costs (billed separately)

## 5. Maintenance Windows

### 5.1 Scheduled Maintenance

Provider may perform Scheduled Maintenance:

- **Standard Window**: Sundays 2:00 AM - 6:00 AM ET (Customer's primary region)
- **Maximum Duration**: 4 hours per month
- **Notice Period**: 7 days minimum for non-urgent maintenance
- **Communication**: Email notification to designated administrators

### 5.2 Emergency Maintenance

Emergency Maintenance may occur without advance notice for:

- Critical security vulnerabilities requiring immediate patching
- Imminent system stability threats
- Compliance with urgent legal or regulatory requirements

Provider will:

- Notify customers as soon as practicable
- Minimize duration to the extent possible
- Provide post-incident summary within 48 hours

### 5.3 Maintenance Exclusions

Downtime during the following is not counted against the SLA:

- Scheduled Maintenance windows
- Emergency Maintenance periods
- Customer-requested maintenance

## 6. Exclusions

The SLA does not apply to Downtime caused by:

### 6.1 Customer Actions

- Misconfiguration of customer-managed settings
- Customer's application code or integrations
- Actions contrary to documentation or best practices
- Exceeding service limits or quotas

### 6.2 External Factors

- Internet connectivity issues outside Provider's control
- Third-party service provider failures (e.g., Clearinghouse SFTP, customer backend systems)
- Force majeure events (natural disasters, war, government actions)
- DNS issues outside Provider's control

### 6.3 Azure Platform

- Azure platform-level outages (governed by Microsoft's SLA)
- Regional Azure service disruptions
- Azure service quotas or capacity constraints

### 6.4 Beta Features

- Features designated as "Beta" or "Preview"
- Experimental functionality not generally available

## 7. Support Services

### 7.1 Support Tiers

| Plan | Support Hours | Response Time (P1) | Response Time (P2) |
|------|--------------|-------------------|-------------------|
| Starter | Business hours (M-F, 9-5 ET) | 8 hours | 24 hours |
| Professional | Extended (M-F, 7AM-9PM ET) | 4 hours | 8 hours |
| Enterprise | 24/7/365 | 1 hour | 4 hours |

### 7.2 Priority Definitions

**Priority 1 (Critical)**: Production system down, no workaround available, significant business impact

**Priority 2 (High)**: Major functionality impaired, workaround available but not sustainable

**Priority 3 (Medium)**: Functionality impaired, reasonable workaround available

**Priority 4 (Low)**: General questions, minor issues, enhancement requests

### 7.3 Support Channels

| Plan | Email | Phone | Chat | Dedicated TAM |
|------|-------|-------|------|---------------|
| Starter | ✓ | - | - | - |
| Professional | ✓ | ✓ | ✓ | - |
| Enterprise | ✓ | ✓ | ✓ | ✓ |

### 7.4 Escalation Path

1. **Tier 1**: Support Engineer (initial response)
2. **Tier 2**: Senior Support Engineer (within 4 hours for P1)
3. **Tier 3**: Engineering Team (within 8 hours for P1)
4. **Executive**: Director of Customer Success (P1 unresolved > 24 hours)

## 8. Incident Communication

### 8.1 Status Page

Provider maintains a public status page at: https://status.cloudhealthoffice.com

- Real-time system status for all components
- Incident updates every 30 minutes during active incidents
- Historical uptime and incident data

### 8.2 Incident Notifications

**During Incidents:**

- Initial notification: Within 15 minutes of detection
- Status updates: Every 30 minutes
- Resolution notification: Within 1 hour of resolution

**Post-Incident:**

- Root Cause Analysis (RCA): Within 5 business days for P1 incidents
- Corrective action plan: Included in RCA
- Customer review call: Available upon request (Enterprise plan)

### 8.3 Notification Methods

| Plan | Status Page | Email | SMS | Phone |
|------|------------|-------|-----|-------|
| Starter | ✓ | ✓ | - | - |
| Professional | ✓ | ✓ | ✓ | - |
| Enterprise | ✓ | ✓ | ✓ | ✓ |

## 9. Data Protection SLA

### 9.1 Data Durability

- **Durability Target**: 99.99999999999% (11 nines)
- **Technology**: Azure Geo-Redundant Storage (Standard/Enterprise plans)
- **Replication**: Synchronous within region, asynchronous across regions

### 9.2 Backup and Recovery

| Plan | Backup Frequency | Retention | RTO | RPO |
|------|-----------------|-----------|-----|-----|
| Starter | Daily | 30 days | 24 hours | 24 hours |
| Professional | Every 4 hours | 90 days | 4 hours | 4 hours |
| Enterprise | Continuous | 365 days | 1 hour | 15 minutes |

**RTO**: Recovery Time Objective (time to restore service)  
**RPO**: Recovery Point Objective (maximum acceptable data loss)

### 9.3 Disaster Recovery

- **DR Site**: Paired Azure region for all plans
- **Failover Time**: < 4 hours (Enterprise: < 1 hour)
- **DR Testing**: Annual testing with customer notification

## 10. HIPAA Compliance Commitments

### 10.1 Security Controls

Provider maintains HIPAA-required safeguards:

- Encryption at rest (AES-256) and in transit (TLS 1.2+)
- Access logging and monitoring (365-day retention)
- Regular vulnerability scanning and penetration testing
- Employee background checks and training

### 10.2 Audit Support

Provider will:

- Provide compliance documentation upon request
- Support customer HIPAA audits with reasonable notice
- Make security assessment reports available (under NDA)

### 10.3 Breach Response

- Detection to notification: < 24 hours
- Forensic investigation initiation: < 4 hours
- Incident documentation: Complete within 48 hours
- Regulatory reporting support: As required by law

## 11. Measurement and Reporting

### 11.1 Monitoring

Provider monitors service availability using:

- Synthetic monitoring from multiple geographic locations
- Real-time health probes every 60 seconds
- Customer-accessible monitoring dashboard

### 11.2 Monthly Reports

Available via customer portal:

- Uptime percentage by service component
- Incident summary and impact
- Performance metrics (response times, throughput)
- Credit balance and history

### 11.3 Quarterly Business Reviews (Enterprise)

- Dedicated review session with account team
- Performance trending and analysis
- Roadmap preview and feedback
- Security and compliance updates

## 12. SLA Modifications

Provider may modify this SLA with:

- **Improvements**: Effective immediately
- **Material Changes**: 30 days' advance notice
- **Notification**: Email to designated administrators

Continued use after notice period constitutes acceptance of modified terms.

## 13. Governing Documents

In the event of conflict between this SLA and other agreements:

1. Business Associate Agreement (for HIPAA matters)
2. Master Services Agreement
3. This SLA
4. Product documentation

## 14. Contact Information

**Support Portal**: https://support.cloudhealthoffice.com  
**Status Page**: https://status.cloudhealthoffice.com  
**Support Email**: support@cloudhealthoffice.com  
**Enterprise Support**: enterprise-support@cloudhealthoffice.com

---

## Appendix A: Historical Uptime

Provider publishes historical uptime metrics at https://status.cloudhealthoffice.com/history

## Appendix B: Regional Availability

| Azure Region | Availability | DR Pair |
|--------------|--------------|---------|
| East US | Primary | West US 2 |
| East US 2 | Primary | Central US |
| West US 2 | Primary | East US |
| Central US | Primary | East US 2 |

---

**Cloud Health Office** – Advancing Healthcare EDI Integration  
**Source-Available (BSL 1.1) | Azure-Native | HIPAA-Compliant**

© 2025 Aurelianware. All rights reserved.
