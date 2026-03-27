# Cloud Health Office Support Terms

**Effective Date**: December 1, 2024  
**Last Updated**: December 1, 2024  
**Version**: 1.0

---

## 1. Overview

These Support Terms describe the technical support services ("Support Services") provided by Aurelianware ("Provider") for the Cloud Health Office platform. These terms supplement your Master Services Agreement or applicable subscription terms.

## 2. Scope of Support

### 2.1 Included Support Services

Support Services include assistance with:

- **Platform Operation**: Configuration, deployment, and operation of Cloud Health Office
- **Issue Resolution**: Troubleshooting and resolving platform-related issues
- **EDI Processing**: X12 transaction processing questions and issues
- **FHIR Integration**: FHIR R4 API implementation and troubleshooting
- **Security**: Security-related questions and incident assistance
- **Compliance**: HIPAA compliance guidance for platform usage
- **Upgrades**: Assistance with platform updates and migrations

### 2.2 Excluded from Support

Support Services do not include:

- **Custom Development**: Building custom integrations or workflows
- **Third-Party Systems**: Issues with non-Cloud Health Office systems
- **Training**: Formal training sessions (available separately)
- **Consulting**: Strategic consulting or implementation planning
- **Legacy Versions**: Support for deprecated platform versions
- **Customer Data Recovery**: Recovery of customer-deleted data

### 2.3 Languages

Support Services are available in English. Other languages may be available upon request for Enterprise customers.

## 3. Support Plans

### 3.1 Plan Comparison

| Feature | Starter | Professional | Enterprise |
|---------|---------|--------------|------------|
| **Support Hours** | Business hours | Extended hours | 24/7/365 |
| **Email Support** | ✓ | ✓ | ✓ |
| **Phone Support** | - | ✓ | ✓ |
| **Live Chat** | - | ✓ | ✓ |
| **P1 Response Time** | 8 hours | 4 hours | 1 hour |
| **P2 Response Time** | 24 hours | 8 hours | 4 hours |
| **Named Contacts** | 2 | 5 | Unlimited |
| **Dedicated TAM** | - | - | ✓ |
| **Quarterly Reviews** | - | - | ✓ |
| **Root Cause Analysis** | - | ✓ | ✓ |
| **Proactive Monitoring** | - | - | ✓ |

### 3.2 Support Hours

| Plan | Hours | Timezone |
|------|-------|----------|
| Starter | Monday-Friday, 9:00 AM - 5:00 PM | Eastern Time (ET) |
| Professional | Monday-Friday, 7:00 AM - 9:00 PM | Eastern Time (ET) |
| Enterprise | 24 hours, 7 days per week | Customer's local timezone |

Holiday schedules are published annually. Emergency support for P1 issues is available for all plans outside business hours.

## 4. Issue Priority Levels

### 4.1 Priority Definitions

**Priority 1 (Critical)**

- Production system completely unavailable
- PHI breach or security incident
- No workaround available
- Significant business/financial impact
- Example: All EDI processing halted, FHIR APIs returning errors

**Priority 2 (High)**

- Major functionality severely degraded
- Workaround available but not sustainable
- Significant number of users affected
- Example: 837 claims failing at 50% rate, Integration Account errors

**Priority 3 (Medium)**

- Functionality partially impaired
- Reasonable workaround available
- Limited user impact
- Example: Dashboard loading slowly, non-critical alerts not firing

**Priority 4 (Low)**

- Minor issues or inconveniences
- Questions about functionality
- Feature requests
- Documentation clarifications
- Example: How to configure a new trading partner

### 4.2 Response Time Objectives

| Priority | Starter | Professional | Enterprise |
|----------|---------|--------------|------------|
| P1 (Critical) | 8 business hours | 4 hours | 1 hour |
| P2 (High) | 24 business hours | 8 business hours | 4 hours |
| P3 (Medium) | 3 business days | 2 business days | 1 business day |
| P4 (Low) | 5 business days | 3 business days | 2 business days |

**Response Time** = Time from ticket submission to first meaningful response from support team.

### 4.3 Resolution Targets

| Priority | Target Resolution Time | Escalation Trigger |
|----------|----------------------|-------------------|
| P1 (Critical) | 4 hours | Immediate escalation |
| P2 (High) | 1 business day | 8 hours without progress |
| P3 (Medium) | 3 business days | 2 days without progress |
| P4 (Low) | 7 business days | 5 days without progress |

Note: Resolution targets are goals, not guarantees. Complex issues may require longer resolution times.

## 5. Support Channels

### 5.1 Support Portal

**URL**: https://support.cloudhealthoffice.com

The primary channel for all support requests. Features:

- Ticket submission and tracking
- Knowledge base access
- Status updates and notifications
- Ticket history and documentation
- Self-service diagnostics

### 5.2 Email Support

**Address**: support@cloudhealthoffice.com

Available for all plans. Emails create tickets automatically.

### 5.3 Phone Support

**Number**: +1-928-940-2410

Available for Professional and Enterprise plans during support hours.

For P1 emergencies outside hours, an on-call engineer will respond within 30 minutes.

### 5.4 Live Chat

Available for Professional and Enterprise plans via the support portal during support hours.

### 5.5 Dedicated Support (Enterprise)

Enterprise customers receive:

- Dedicated Technical Account Manager (TAM)
- Direct engineering escalation path
- Private Slack or Teams channel (upon request)
- Executive sponsor contact

## 6. Submitting Support Requests

### 6.1 Required Information

To expedite resolution, include:

- **Customer Name**: Organization and subscription ID
- **Contact Information**: Name, email, phone
- **Priority Level**: P1-P4 based on definitions above
- **Environment**: Production, UAT, or Development
- **Description**: Clear description of the issue
- **Steps to Reproduce**: If applicable
- **Error Messages**: Complete error text and codes
- **Timestamps**: When the issue occurred
- **Impact**: Number of users/transactions affected
- **Troubleshooting Steps**: What you've already tried

### 6.2 Best Practices

- Submit one issue per ticket
- Attach relevant logs and screenshots
- Provide sanitized examples (no real PHI)
- Update tickets with new information
- Respond promptly to questions

### 6.3 PHI in Support Requests

**Never include real PHI in support tickets.**

If diagnostic data is needed:

- Use synthetic/test data when possible
- Mask PHI elements (names, IDs, dates)
- Use secure file sharing for necessary logs
- Provider will advise on secure transfer methods

## 7. Escalation Process

### 7.1 Standard Escalation Path

1. **Tier 1**: Support Engineer (initial triage)
2. **Tier 2**: Senior Support Engineer (technical escalation)
3. **Tier 3**: Platform Engineering Team (product escalation)
4. **Management**: Director of Customer Success (management escalation)
5. **Executive**: VP of Engineering (executive escalation)

### 7.2 Automatic Escalation

Issues are automatically escalated based on:

| Condition | Escalation |
|-----------|------------|
| P1 open > 4 hours | Tier 3 + Management |
| P1 open > 8 hours | Executive notification |
| P2 open > 24 hours | Tier 3 escalation |
| Customer request | Immediate escalation |

### 7.3 How to Escalate

- Update ticket with "ESCALATION REQUEST" and reason
- Email: escalations@cloudhealthoffice.com
- Phone (Enterprise): Contact your TAM directly
- Reference ticket number in all communications

## 8. Named Support Contacts

### 8.1 Contact Limits

| Plan | Named Contacts |
|------|---------------|
| Starter | 2 |
| Professional | 5 |
| Enterprise | Unlimited |

### 8.2 Contact Requirements

Named contacts must:

- Be employees or authorized contractors of customer
- Have appropriate technical knowledge
- Complete platform familiarization
- Be designated in writing

### 8.3 Managing Contacts

- Add/remove contacts via support portal
- Changes effective within 1 business day
- Annual review recommended

## 9. Support for Platform Updates

### 9.1 Release Types

| Release Type | Frequency | Notice | Support |
|--------------|-----------|--------|---------|
| Patch | As needed | None | Automatic |
| Minor | Monthly | 7 days | Assisted |
| Major | Quarterly | 30 days | Assisted |

### 9.2 Update Support

Provider will:

- Provide release notes and upgrade guides
- Assist with update planning (Professional/Enterprise)
- Support rollback if critical issues arise
- Offer extended support for previous version (90 days)

### 9.3 Deprecated Features

- 90 days' notice before feature deprecation
- Migration guidance provided
- Support for deprecated features ends with notice period

## 10. Knowledge Resources

### 10.1 Self-Service Resources

- **Documentation Portal**: https://docs.cloudhealthoffice.com
- **Knowledge Base**: https://support.cloudhealthoffice.com/kb
- **API Reference**: https://api.cloudhealthoffice.com/docs
- **GitHub Repository**: https://github.com/aurelianware/cloudhealthoffice
- **Community Forums**: https://community.cloudhealthoffice.com

### 10.2 Training (Available Separately)

- Platform Administration (8 hours)
- EDI Transaction Processing (4 hours)
- FHIR Integration (6 hours)
- Security Best Practices (4 hours)

Contact your account manager for training options.

## 11. Customer Responsibilities

### 11.1 General Responsibilities

Customers are responsible for:

- Maintaining accurate contact information
- Designating qualified named contacts
- Providing timely and accurate information for issues
- Implementing recommended solutions and updates
- Reviewing and following documentation
- Maintaining customer-side integrations

### 11.2 Security Responsibilities

Customers must:

- Secure access credentials and API keys
- Follow security best practices
- Report suspected security incidents immediately
- Cooperate with security investigations

### 11.3 Cooperation

Customers agree to:

- Make systems available for troubleshooting
- Provide reasonable access to technical staff
- Test fixes and provide feedback
- Participate in root cause analysis when requested

## 12. Support Limitations

### 12.1 Reasonable Use

Support Services are intended for incident resolution and guidance, not:

- Day-to-day operations
- Staff augmentation
- Implementation services
- Training replacement

### 12.2 Complex Issues

Some issues may require:

- Additional time for investigation
- Customer testing and validation
- Coordination with third parties
- Professional services engagement

### 12.3 Unsupported Configurations

Provider may decline support for:

- Unsupported or end-of-life versions
- Customer modifications to platform code
- Configurations contrary to documentation
- Third-party components causing issues

## 13. Feedback and Satisfaction

### 13.1 Post-Ticket Surveys

After ticket resolution:

- Survey sent within 24 hours
- Brief satisfaction rating requested
- Comments welcomed for improvement
- Responses reviewed by management

### 13.2 Quarterly Reviews (Enterprise)

- Review of support metrics and trends
- Discussion of open issues
- Feedback on support quality
- Planning for upcoming needs

### 13.3 Providing Feedback

- Ticket comments: Direct feedback on specific issues
- Survey responses: Post-resolution feedback
- Email: feedback@cloudhealthoffice.com
- TAM conversations: Strategic feedback (Enterprise)

## 14. Changes to Support Terms

Provider may modify these Support Terms with:

- **Service Improvements**: Effective immediately
- **Material Changes**: 30 days' advance notice
- **Price Changes**: 60 days' advance notice (at renewal)

Customers will be notified via email to designated support contacts.

## 15. Contact Information

**Support Portal**: https://support.cloudhealthoffice.com  
**Support Email**: support@cloudhealthoffice.com  
**Phone (Professional/Enterprise)**: +1-928-940-2410  
**Escalations**: escalations@cloudhealthoffice.com  
**Feedback**: feedback@cloudhealthoffice.com

---

## Appendix A: Severity Examples

| Severity | Example Issues |
|----------|---------------|
| P1 | All workflows stopped; security breach; data loss |
| P2 | 50%+ transaction failures; API outages; integration down |
| P3 | Slow performance; intermittent errors; UI issues |
| P4 | Questions; documentation gaps; feature requests |

## Appendix B: Support Contact Template

```
Subject: [Priority: P1/P2/P3/P4] Brief description

Customer: [Organization Name]
Subscription ID: [ID]
Contact: [Name, Email, Phone]

Environment: [Production/UAT/Development]
Region: [Azure Region]

Issue Description:
[Detailed description of the issue]

Steps to Reproduce:
1. [Step 1]
2. [Step 2]
3. [Step 3]

Expected Behavior:
[What should happen]

Actual Behavior:
[What is happening]

Error Messages:
[Complete error text]

Impact:
[Number of users/transactions affected]

Troubleshooting Completed:
[Steps already taken]

Attachments:
[Sanitized logs, screenshots - NO PHI]
```

---

**Cloud Health Office** – Advancing Healthcare EDI Integration  
**Source-Available (BSL 1.1) | Azure-Native | HIPAA-Compliant**

© 2025 Aurelianware. All rights reserved.
