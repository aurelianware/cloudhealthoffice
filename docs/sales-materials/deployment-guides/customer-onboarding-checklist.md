# Customer Onboarding Checklist - SaaS Model
## Cloud Health Office Platform - Beta Launch v4.0

**Target Timeline**: 7 days from contract signature to production go-live  
**Model**: Multi-tenant SaaS (Cloud Health Office manages all infrastructure)  
**Version**: 2.0 (SaaS-optimized)

Use this streamlined checklist to track progress through the customer onboarding process from contract signature to go-live.

---

## Pre-Sales (Before Contract)

### Discovery & Qualification
- [ ] Initial discovery call completed (30 minutes)
- [ ] Current pain points documented (manual processes, compliance gaps, cost)
- [ ] Transaction volume estimated (by type: 837, 275, 270/271, 278)
- [ ] Claims system identified and API availability confirmed
- [ ] Clearinghouse(s) identified and SFTP access confirmed
- [ ] Decision makers identified (technical lead, compliance officer, exec sponsor)
- [ ] Budget and timeline confirmed
- [ ] Compliance requirements documented (HIPAA, state-specific)

### Demo & Proposal
- [ ] Product demo completed (live platform walkthrough)
- [ ] ROI analysis prepared using sales calculator
- [ ] Security and compliance discussion held
- [ ] Subscription tier recommended: ☐ Starter  ☐ Professional  ☐ Enterprise
- [ ] Beta program benefits explained (if applicable)
- [ ] Formal proposal submitted via Order Form
- [ ] Proposal questions answered

### Contract & Legal
- [ ] Order Form executed
- [ ] Business Associate Agreement (BAA) signed
- [ ] Terms of Service acceptance confirmed
- [ ] Payment method configured in Stripe
- [ ] Initial payment received (first month/year subscription)
- [ ] Welcome email sent with credentials (automated)

---

## Day 1-2: Tenant Provisioning & Configuration (Cloud Health Office)

**Owner**: Cloud Health Office Implementation Team  
**Customer Action**: Minimal (provide configuration details)

### Tenant Provisioning (Automated)
- [ ] SaaS tenant created in Cloud Health Office platform
- [ ] Unique tenant ID assigned: __________________
- [ ] SFTP account provisioned (credentials generated via portal, not sent via email)
  - Host: sftp.cloudhealthoffice.com
  - Credentials: Customer generates via secure portal
- [ ] Portal access configured (Azure AD SSO)
- [ ] API key generation enabled in portal (customer-controlled)
- [ ] Data Lake storage allocated (1TB included per tier; $50/TB/month overage applies to all tiers)
- [ ] Service Bus topics created (attachments-in, rfai-requests, edi-278)
- [ ] Application Insights workspace configured
- [ ] Monitoring dashboard created

### Customer Information Collected
- [ ] Organization legal name: __________________
- [ ] Payer ID (X12): __________________
- [ ] X12 Qualifier: __________________
- [ ] Primary contact: __________ | Email: __________ | Phone: __________
- [ ] Technical contact: __________ | Email: __________ | Phone: __________
- [ ] Billing contact: __________ | Email: __________ | Phone: __________
- [ ] Subscription tier confirmed: __________________
- [ ] Billing frequency: ☐ Monthly  ☐ Annual

### Claims System Integration Details
- [ ] Claims system vendor: __________________
- [ ] Claims system version: __________________
- [ ] API endpoint URL: __________________
- [ ] Authentication method: ☐ OAuth 2.0  ☐ API Key  ☐ Basic Auth
- [ ] Test API credentials provided by customer
- [ ] Test claim IDs provided (minimum 5 for testing)
- [ ] Required data fields documented (claim number format, member ID format, etc.)

### Clearinghouse/Trading Partner Details
- [ ] Primary clearinghouse: __________________
- [ ] Clearinghouse SFTP host: __________________
- [ ] Clearinghouse SFTP credentials provided by customer
- [ ] Clearinghouse folder paths documented:
  - Inbound: __________________
  - Outbound: __________________
- [ ] X12 file naming convention documented
- [ ] Transaction types to process: ☐ 275  ☐ 277  ☐ 278  ☐ 837  ☐ 270/271

---

## Day 2-3: Configuration & Integration Setup

**Owner**: Cloud Health Office Implementation Team (with customer collaboration)  
**Location**: Customer-deployed portal (for example, `https://portal.<your-domain>`)

### Portal Configuration Wizard (Customer Self-Service)
- [ ] Customer logs into portal successfully
- [ ] Organization profile completed (name, address, contacts)
- [ ] Payer IDs and X12 qualifiers configured
- [ ] Claims system integration configured:
  - [ ] API endpoint entered
  - [ ] Authentication credentials entered (stored in Key Vault)
  - [ ] Field mappings configured (X12 fields → Claims system fields)
  - [ ] Test connection successful (green checkmark)
- [ ] Clearinghouse SFTP configured:
  - [ ] SFTP host, username, password entered
  - [ ] Folder paths entered
  - [ ] Test SFTP connection successful (green checkmark)
- [ ] Transaction types enabled (275, 277, 278, etc.)
- [ ] Notification preferences configured (email alerts, Slack webhooks)

### Backend Integration (Cloud Health Office)
- [ ] Argo Workflow DAGs configured for customer tenant
- [ ] API connection to customer claims system tested successfully
- [ ] SFTP connection to clearinghouse tested successfully
- [ ] C# X12 EDI services configured with customer's trading partner IDs
- [ ] Field mapping logic implemented and tested
- [ ] Error handling and retry policies configured
- [ ] Monitoring alerts configured (failure notifications)

### Security Configuration
- [ ] Customer credentials stored securely in Azure Key Vault
- [ ] Managed identity permissions configured
- [ ] Network security validated (HTTPS only, TLS 1.3)
- [ ] PHI masking validated in logs
- [ ] Audit logging enabled (7-year retention)

---

## Day 3-5: Testing & Validation

**Owner**: Both (Cloud Health Office supports, customer validates)

### Unit Testing (Cloud Health Office)
- [ ] Test 1: Valid 275 file processing - PASS/FAIL: ____
- [ ] Test 2: Invalid claim number handling - PASS/FAIL: ____
- [ ] Test 3: Malformed EDI file handling - PASS/FAIL: ____
- [ ] Test 4: Claims system API timeout handling - PASS/FAIL: ____
- [ ] Test 5: Duplicate file detection - PASS/FAIL: ____
- [ ] Test 6: Large file processing (>5MB) - PASS/FAIL: ____
- [ ] All unit tests passed

### Customer Acceptance Testing (Customer-Led)
- [ ] Customer UAT plan provided (test scripts, expected outcomes)
- [ ] Customer processes 5+ test transactions end-to-end
- [ ] Test transactions archived correctly in Data Lake
- [ ] Data appears correctly in customer claims system
- [ ] No PHI visible in Application Insights logs
- [ ] Error handling validated (customer tests error scenarios)
- [ ] Monitoring dashboard reviewed and understood by customer
- [ ] Customer UAT sign-off received

**UAT Results**:
- Total test cases executed: ______
- Passed: ______
- Failed: ______
- Success rate: ______%

### Performance Validation
- [ ] Average transaction processing time: ____ seconds (target: < 30 seconds)
- [ ] 95th percentile processing time: ____ seconds (target: < 60 seconds)
- [ ] SFTP polling frequency verified (every 5 minutes)
- [ ] No performance issues identified

---

## Day 5-6: Training & Documentation

**Owner**: Cloud Health Office Customer Success

### Training Sessions
- [ ] Session 1: Platform Overview & Portal Navigation (1 hour)
  - Date/Time: ______________
  - Attendees: ______________
  - Recording link: ______________
- [ ] Session 2: Monitoring & Troubleshooting (1 hour)
  - Date/Time: ______________
  - Attendees: ______________
  - Recording link: ______________

### Documentation Delivered
- [ ] Quickstart guide shared
- [ ] Portal user guide shared
- [ ] Monitoring dashboard guide shared
- [ ] Troubleshooting guide shared
- [ ] Escalation procedures shared
- [ ] API documentation shared (if using APIs)
- [ ] Security and compliance documentation shared
- [ ] SLA documentation shared

### Knowledge Transfer
- [ ] Customer operations team trained on portal
- [ ] Customer technical team trained on monitoring
- [ ] Customer support contacts saved in portal
- [ ] Cloud Health Office support contacts saved by customer
- [ ] Escalation path documented and understood

---

## Day 7: Production Go-Live

**Owner**: Both (with Cloud Health Office on standby)

### Pre-Go-Live Checklist (Morning)
- [ ] Go-live kickoff call completed (8:00 AM)
- [ ] All systems status verified (portal, SFTP, claims system)
- [ ] Cloud Health Office support team on standby
- [ ] Customer operations team ready
- [ ] Rollback plan reviewed (if needed)

### Go-Live Execution (Throughout Day)
- [ ] Production SFTP credentials activated (if different from test)
- [ ] First production file processed successfully
- [ ] First 10 files processed successfully
- [ ] No errors in monitoring dashboard
- [ ] Claims system data validated by customer
- [ ] Midday status call completed (12:00 PM)

### End-of-Day Review (Evening)
- [ ] Day 1 metrics reviewed:
  - Files processed: ______
  - Success rate: ______%
  - Average processing time: ____ seconds
  - Errors: ______
- [ ] Any issues addressed and resolved
- [ ] End-of-day review call completed (5:00 PM)
- [ ] Day 1 summary email sent to customer

---

## Day 8-14: Post-Go-Live Support

**Owner**: Cloud Health Office Customer Success

### Daily Check-Ins (Days 8-10)
- [ ] Day 8 status call - Issues: __________________
- [ ] Day 9 status call - Issues: __________________
- [ ] Day 10 status call - Issues: __________________

### Weekly Check-In (Days 11-14)
- [ ] Week 1 review call completed
- [ ] Metrics reviewed (success rate, processing time, errors)
- [ ] Customer feedback collected
- [ ] Any optimization recommendations provided

---

## Week 2-4: Transition to Steady State

### Week 2: Stabilization
- [ ] Daily monitoring by customer operations team
- [ ] Cloud Health Office support available (email, portal tickets)
- [ ] No critical issues reported
- [ ] Performance meets SLA commitments

### Week 3: Optimization
- [ ] Weekly status call with customer
- [ ] Performance tuning recommendations provided (if applicable)
- [ ] Customer operations team fully self-sufficient
- [ ] Documentation updated based on learnings

### Week 4: 30-Day Review
- [ ] 30-day review call scheduled and completed
- [ ] 30-day metrics report generated:
  - Total files processed: ______
  - Success rate: ______%
  - Average processing time: ____ seconds
  - Uptime: ______%
  - Support tickets: ______
- [ ] Customer satisfaction survey completed
- [ ] ROI tracking established
- [ ] Success story documented (if applicable)
- [ ] Customer willing to provide testimonial: ☐ Yes  ☐ No  ☐ Later

---

## Ongoing Success (Monthly/Quarterly)

### Monthly Activities
- [ ] Monthly metrics review (automated email report)
- [ ] Monthly check-in call (optional, if customer requests)
- [ ] Platform updates communicated
- [ ] Customer feedback collected

### Quarterly Activities
- [ ] Quarterly Business Review (QBR) held
- [ ] ROI analysis updated
- [ ] Roadmap and future features discussed
- [ ] Training refresher offered (if needed)
- [ ] Contract renewal discussion (if approaching)

### Annual Activities
- [ ] Annual health check completed
- [ ] Security assessment refreshed
- [ ] Disaster recovery test conducted (if Enterprise tier)
- [ ] Contract renewal processed
- [ ] Year-in-review report delivered

---

## Success Criteria

### Technical Success
- [ ] 95%+ transaction success rate in production
- [ ] < 30 seconds average processing time (meets SLA)
- [ ] Uptime meets tier commitment (99.5% / 99.9% / 99.95%)
- [ ] Zero security incidents
- [ ] Zero data loss incidents

### Business Success
- [ ] Customer achieving expected ROI (tracked monthly)
- [ ] Reduction in manual processing time (80%+ target)
- [ ] Customer satisfaction ≥ 4/5
- [ ] Customer willing to provide reference
- [ ] Renewal commitment secured (if approaching end of term)

### Operational Success
- [ ] Customer operations team self-sufficient (no daily support needed)
- [ ] Support ticket volume declining over time
- [ ] Proactive monitoring alerts working correctly
- [ ] Documentation complete and up-to-date
- [ ] Knowledge transfer complete

---

## Key Differences: SaaS vs. Self-Hosted

**SaaS Model (This Checklist)**:
- ✅ No Azure subscription required (Cloud Health Office manages infrastructure)
- ✅ No resource provisioning needed (automated by Cloud Health Office)
- ✅ No infrastructure deployment (AKS, Argo Workflows, Storage, Service Bus pre-configured)
- ✅ No Azure cost management needed (included in subscription)
- ✅ Faster onboarding (7 days vs. 12 weeks for self-hosted)
- ✅ Lower complexity (customer focuses on configuration, not infrastructure)

**Self-Hosted Model** (Not covered in this checklist):
- Customer provides Azure subscription
- Customer deploys Bicep templates
- Customer manages Azure costs directly
- Customer responsible for infrastructure patching/updates
- Longer onboarding timeline (12 weeks typical)

---

## Final Sign-Off

### Customer Approval

**I certify that the Cloud Health Office Platform has been successfully deployed and meets all acceptance criteria.**

Customer Name: ______________________________  
Title: ______________________________________  
Signature: __________________________________  
Date: _______________________________________

### Cloud Health Office Confirmation

**I certify that all deliverables have been completed and the customer has been successfully onboarded.**

Implementation Engineer: _____________________  
Customer Success Manager: ___________________  
Signature: __________________________________  
Date: _______________________________________

---

## Appendix: Key Contact Information

### Customer Contacts
- **Executive Sponsor:** _________________ | _________________ | _________________
- **Project Manager:** __________________ | _________________ | _________________
- **Technical Lead:** ___________________ | _________________ | _________________
- **Operations Lead:** __________________ | _________________ | _________________

### Cloud Health Office Contacts
- **Implementation Engineer:** __________________ | _________________ | _________________
- **Customer Success Manager:** _________________ | _________________ | _________________
- **Technical Support:** support@cloudhealthoffice.com | 1-888-CLOUD-55
- **Sales Representative:** __________________ | _________________ | _________________

---

## Document Control

- **Version:** 2.0 (SaaS-optimized)
- **Created:** February 17, 2026
- **Last Updated:** February 17, 2026
- **Next Review:** May 17, 2026
- **Owner:** Customer Success Team

---

**Cloud Health Office** – The Inevitable Evolution of Healthcare EDI  
**Source-Available (BSL 1.1) | Azure-Native | HIPAA-Compliant | CMS-0057-F Ready**

© 2026 Aurelianware. All rights reserved.

---

*This checklist should be updated throughout the customer onboarding process and retained for reference and continuous improvement.*
