# Customer Onboarding Checklist
## HIPAA Attachments Platform - Phase 1

Use this checklist to track progress through the customer onboarding process from contract signature to go-live.

---

## Pre-Sales (Before Contract)

### Discovery & Qualification
- [ ] Initial discovery call completed
- [ ] Current process documented (hours per attachment, volume, pain points)
- [ ] Claims system identified and validated (API availability confirmed)
- [ ] Availity SFTP access confirmed
- [ ] Decision makers identified
- [ ] Budget and timeline confirmed
- [ ] Compliance requirements documented

### Demo & Proposal
- [ ] Product demo completed
- [ ] ROI analysis prepared and reviewed
- [ ] Technical architecture review completed
- [ ] Security and compliance discussion held
- [ ] References provided and contacted
- [ ] Formal proposal submitted
- [ ] Proposal questions answered

### Contract & Legal
- [ ] Master Services Agreement executed
- [ ] Business Associate Agreement (BAA) signed
- [ ] Statement of Work signed
- [ ] Security and compliance exhibits reviewed
- [ ] Payment terms agreed upon
- [ ] Initial payment received (50% implementation fee)

---

## Week 1-2: Discovery & Planning

### Project Initiation
- [ ] Kickoff meeting scheduled and completed
- [ ] Project team members assigned (both sides)
- [ ] RACI matrix created
- [ ] Communication plan established
- [ ] Weekly status call scheduled
- [ ] Shared workspace created (SharePoint/Teams)
- [ ] Project charter signed off

### Technical Discovery
- [ ] Availity SFTP credentials received
  - Host: ___________________________
  - Username: ________________________
  - Inbound folder: __________________
- [ ] SFTP connectivity tested successfully
- [ ] Sample 275 files obtained (minimum 3)
- [ ] File naming conventions documented

### Claims System Discovery
- [ ] Claims system vendor and version confirmed
  - Vendor: __________________________
  - Version: _________________________
  - Environment: _____________________
- [ ] API documentation received and reviewed
- [ ] API credentials received
  - Endpoint: ________________________
  - Auth method: _____________________
- [ ] Test API connectivity successful
- [ ] Required API endpoints identified:
  - [ ] Claim lookup/validation endpoint
  - [ ] Attachment link endpoint
  - [ ] Status update endpoint (if applicable)
- [ ] API rate limits documented
- [ ] Test claim IDs provided for testing

### Business Rules
- [ ] Valid claim statuses for attachment processing defined
- [ ] Required data fields identified (claim number, member ID, provider NPI, etc.)
- [ ] Error handling requirements documented
- [ ] Retry policies agreed upon
- [ ] Notification preferences defined (alerts, reports)

### Configuration
- [ ] Customer configuration file created (`config/customers/[customer-name].json`)
- [ ] Payer ID for X12 transactions confirmed: __________________
- [ ] X12 qualifier confirmed: __________________
- [ ] Field mappings documented (X12 → Claims System)
- [ ] Configuration reviewed and approved by customer

### Infrastructure Planning
- [ ] Azure subscription determined (customer's or shared)
- [ ] Azure region selected: __________________
- [ ] Resource naming convention agreed upon
- [ ] Estimated monthly Azure costs reviewed: $__________
- [ ] Cost tracking and billing arranged

### Security & Compliance
- [ ] Security requirements documented
- [ ] Compliance certifications reviewed (SOC 2, HIPAA)
- [ ] PHI masking requirements defined
- [ ] Key Vault secrets structure planned
- [ ] Private endpoint requirements identified
- [ ] Network access requirements documented (firewall rules, VPN, etc.)

### Deliverables
- [ ] Technical specification document approved
- [ ] API integration design approved
- [ ] Field mapping spreadsheet approved
- [ ] Error handling flowchart approved
- [ ] Security implementation plan approved
- [ ] Project timeline agreed upon

---

## Week 3-4: Environment Setup

### DEV Environment - Azure Infrastructure
- [ ] Resource group created: __________________________
- [ ] Logic App Standard deployed: __________________
- [ ] Storage Account (Data Lake Gen2) deployed: __________________
- [ ] Service Bus Namespace deployed: __________________
- [ ] Service Bus Topics created:
  - [ ] `attachments-in`
  - [ ] `rfai-requests`
  - [ ] `edi-278`
- [ ] Application Insights deployed: __________________
- [ ] Key Vault deployed: __________________
- [ ] All resources tagged appropriately

### Key Vault Configuration
- [ ] SFTP credentials stored:
  - [ ] `AvilitySftpUsername`
  - [ ] `AvilitySftpPassword`
- [ ] Claims system credentials stored:
  - [ ] API key/token secret name: __________________
  - [ ] OAuth credentials (if applicable)
- [ ] Other secrets stored (document names):
  - [ ] __________________________
  - [ ] __________________________
- [ ] Logic App granted Key Vault access (managed identity)

### API Connections
- [ ] SFTP-SSH connection created and authenticated
- [ ] Azure Blob connection created (managed identity)
- [ ] Service Bus connection created (managed identity)
- [ ] Integration Account connection created

### Integration Account
- [ ] Integration Account created: __________________
- [ ] X12 schemas uploaded:
  - [ ] X12_005010X210_275
  - [ ] X12_005010X212_277
  - [ ] X12_005010X217_278
- [ ] Trading partners configured:
  - [ ] Availity (030240928)
  - [ ] Customer Payer ([payer-id])
- [ ] X12 agreements created:
  - [ ] Availity-to-Customer (receive 275)
  - [ ] Customer-to-Availity (send 277)

### Monitoring Setup
- [ ] Application Insights workspace configured
- [ ] Log Analytics workspace configured
- [ ] PHI masking rules configured
- [ ] Custom dashboards created
- [ ] Alert rules configured:
  - [ ] Workflow failures
  - [ ] High error rate
  - [ ] SFTP connection failures
  - [ ] Claims system API failures
- [ ] Notification actions configured (email, SMS)

---

## Week 5-7: Integration Development

### Backend Integration
- [ ] Custom claims system connector developed
- [ ] Claim validation logic implemented
- [ ] Attachment linking logic implemented
- [ ] Field mappings implemented per specification
- [ ] Error handling implemented
- [ ] Retry logic configured

### Logic App Workflows
- [ ] `ingest275` workflow configured:
  - [ ] SFTP trigger configured
  - [ ] Data Lake archival action
  - [ ] X12 decode action
  - [ ] Claims system integration actions
  - [ ] Service Bus publish action
  - [ ] Error handling
- [ ] `rfai277` workflow configured (if applicable)
- [ ] `ingest278` workflow configured (if applicable)
- [ ] `replay278` workflow configured (if applicable)

### Workflow Deployment
- [ ] Workflows packaged (workflows.zip)
- [ ] Workflows deployed to DEV Logic App
- [ ] Logic App restarted
- [ ] Workflow runs verified in Azure Portal

### Configuration Updates
- [ ] Workflow parameters configured
- [ ] Connection references validated
- [ ] Managed identity permissions verified
- [ ] Logic App app settings configured

### Code Review & Documentation
- [ ] Code reviewed by senior engineer
- [ ] Configuration reviewed for security
- [ ] Technical documentation updated
- [ ] Operations runbook drafted

---

## Week 8-10: Testing & Validation

### Unit Testing
- [ ] Test 1: Happy path (valid 275 file) - PASS/FAIL: ____
- [ ] Test 2: Invalid claim number - PASS/FAIL: ____
- [ ] Test 3: Malformed EDI file - PASS/FAIL: ____
- [ ] Test 4: Claims system API down - PASS/FAIL: ____
- [ ] Test 5: Duplicate file processing - PASS/FAIL: ____
- [ ] Test 6: Large file (>5MB) - PASS/FAIL: ____
- [ ] All unit tests passed or issues resolved

### Integration Testing
- [ ] End-to-end workflow tested with 10+ files
- [ ] Data Lake archival verified
- [ ] X12 decode accuracy validated
- [ ] Claims system integration verified
- [ ] Service Bus event publishing validated
- [ ] Error handling and retry tested
- [ ] All integration tests passed

### User Acceptance Testing (UAT)
- [ ] UAT environment prepared
- [ ] UAT test plan provided to customer
- [ ] Customer UAT team trained
- [ ] 10+ real 275 files processed successfully
- [ ] Data accuracy verified in claims system
- [ ] Error handling validated
- [ ] PHI masking verified in logs
- [ ] Monitoring dashboard reviewed
- [ ] UAT sign-off received from customer

**UAT Results:**
- Total test cases: ______
- Passed: ______
- Failed: ______
- Success rate: ______%

### Performance Testing
- [ ] Load test: 100 files processed (Duration: ____ minutes)
- [ ] Stress test: 500 files over 8 hours (Success rate: ____%)
- [ ] Average processing time per file: ____ minutes
- [ ] 95th percentile processing time: ____ minutes
- [ ] Performance SLA met (< 5 minutes per file)

### Security Testing
- [ ] Vulnerability scan completed (No critical/high issues)
- [ ] PHI masking validated (No PHI in Application Insights)
- [ ] Key Vault access audit completed
- [ ] Network security verified (private endpoints, NSGs)
- [ ] Penetration test completed (if required)
- [ ] Security assessment report delivered

### Bug Tracking
- [ ] All critical bugs resolved
- [ ] All high-priority bugs resolved
- [ ] Medium/low bugs documented and prioritized
- [ ] Bug fix log maintained

---

## Week 11: Production Deployment

### Production Environment Setup
- [ ] Production resource group created: __________________________
- [ ] All Azure resources deployed to production
- [ ] Production SKU upgraded (WS1 for Logic Apps)
- [ ] Geo-redundant storage enabled
- [ ] Production Key Vault secrets configured
- [ ] Production API connections authenticated
- [ ] Production Integration Account configured

### Production Configuration
- [ ] Private endpoints configured for all PHI resources
- [ ] VNet integration enabled for Logic Apps
- [ ] Azure Backup configured
- [ ] Log retention set to 365 days
- [ ] Production monitoring and alerting configured
- [ ] Production dashboards created

### Production Deployment
- [ ] Workflows deployed to production
- [ ] Production smoke test completed successfully
- [ ] Production credentials validated
- [ ] Production connectivity verified (SFTP, claims system)

### Pre-Go-Live Checklist
- [ ] All production resources deployed and verified
- [ ] Production workflows running successfully
- [ ] Operations runbook finalized and reviewed
- [ ] Support escalation paths defined
- [ ] Backup and DR procedures documented
- [ ] Customer training completed
- [ ] Go-live communication sent to all stakeholders
- [ ] Rollback plan documented and reviewed
- [ ] Go-live support coverage confirmed

---

## Week 12: Training & Go-Live

### Training
- [ ] Session 1: Operations Training (2 hours)
  - Date: ______________
  - Attendees: ________________________
  - Materials distributed
  - Feedback collected
- [ ] Session 2: Technical Training (2 hours)
  - Date: ______________
  - Attendees: ________________________
  - Materials distributed
  - Feedback collected

### Training Materials Delivered
- [ ] Operations runbook
- [ ] Monitoring dashboard guide
- [ ] Error handling flowchart
- [ ] Escalation procedures
- [ ] Technical architecture document
- [ ] Troubleshooting guide
- [ ] Security documentation
- [ ] DR procedures

### Go-Live Preparation
- [ ] Final production smoke test completed (Day before go-live)
- [ ] All training sessions completed
- [ ] Operations runbook distributed to all operators
- [ ] Support contacts shared with customer
- [ ] Implementation team availability confirmed for go-live day
- [ ] Customer operations team ready
- [ ] Go-live kickoff call scheduled

### Go-Live Day Checklist

**Pre-Go-Live (8:00 AM):**
- [ ] Go-live kickoff call completed
- [ ] All systems status verified (green)
- [ ] Support team on standby

**Morning (8:30 AM - 12:00 PM):**
- [ ] First production file processed successfully
- [ ] First 10 files processed successfully
- [ ] No errors in Application Insights
- [ ] Claims system data validated
- [ ] Midday status call completed

**Afternoon (12:00 PM - 5:00 PM):**
- [ ] Continued processing throughout day
- [ ] Real-time monitoring by implementation team
- [ ] Any issues immediately addressed
- [ ] End-of-day review call completed
- [ ] Day 1 summary email sent

**Go-Live Metrics (Day 1):**
- Files processed: ______
- Success rate: ______%
- Average processing time: ____ minutes
- Errors: ______
- Customer satisfaction: ______/5

---

## Week 13-16: Post-Go-Live Support

### Week 13-14: Intensive Support
- [ ] Daily status calls held
- [ ] Daily monitoring by implementation team
- [ ] Daily metrics reports sent to customer
- [ ] All issues addressed within SLA
- [ ] Performance tuning completed based on actual volume
- [ ] Documentation updated based on learnings

### Week 15-16: Transition to Steady State
- [ ] Reduced status calls to weekly
- [ ] Monitoring transitioned to customer operations team
- [ ] Remaining issues/enhancements addressed
- [ ] Customer feedback collected and documented
- [ ] Optimization recommendations provided

### 30-Day Metrics Review
- [ ] 30-day performance report generated
- [ ] ROI tracking established
- [ ] Customer satisfaction survey completed
- [ ] Lessons learned document created
- [ ] Success story documented (if applicable)

**30-Day Metrics:**
- Total files processed: ______
- Success rate: ______%
- Average processing time: ____ minutes
- Uptime: ______%
- Customer satisfaction: ______/5
- ROI achieved: ______x

---

## Ongoing Support & Success

### Monthly Activities
- [ ] Monthly metrics review scheduled
- [ ] Monthly status call with customer
- [ ] Performance optimization recommendations
- [ ] Platform updates communicated
- [ ] Customer satisfaction check-in

### Quarterly Activities
- [ ] Quarterly business review (QBR) held
- [ ] ROI analysis updated
- [ ] Roadmap and future enhancements discussed
- [ ] Training refresher offered
- [ ] Contract renewal discussion (if approaching)

### Annual Activities
- [ ] Annual health check completed
- [ ] Security assessment refreshed
- [ ] Disaster recovery test conducted
- [ ] Contract renewal processed
- [ ] Year-in-review report delivered

---

## Success Criteria

### Technical Success
- [ ] 95%+ success rate in production
- [ ] < 5 minutes average processing time
- [ ] 99.9%+ uptime achieved
- [ ] Zero security incidents
- [ ] Zero data loss incidents

### Business Success
- [ ] Customer achieving expected ROI
- [ ] 80%+ reduction in processing time
- [ ] Customer satisfaction ≥ 4/5
- [ ] Customer willing to provide reference
- [ ] Renewal commitment secured

### Operational Success
- [ ] Customer operations team self-sufficient
- [ ] Support ticket volume declining
- [ ] Proactive monitoring in place
- [ ] Documentation complete and up-to-date
- [ ] Knowledge transfer complete

---

## Final Sign-Off

### Customer Approval

**I certify that the HIPAA Attachments Platform has been successfully deployed and meets all acceptance criteria outlined in the Statement of Work.**

Customer Name: ______________________________
Title: ______________________________________
Signature: __________________________________
Date: _______________________________________

### Provider Confirmation

**I certify that all deliverables have been completed and the customer has been successfully onboarded to the HIPAA Attachments Platform.**

Provider Name: ______________________________
Title: ______________________________________
Signature: __________________________________
Date: _______________________________________

---

## Appendix: Key Contact Information

### Customer Contacts
- **Executive Sponsor:** _________________ | _________________ | _________________
- **Project Manager:** __________________ | _________________ | _________________
- **Technical Lead:** ___________________ | _________________ | _________________
- **Operations Lead:** __________________ | _________________ | _________________

### Provider Contacts
- **Project Manager:** __________________ | _________________ | _________________
- **Technical Lead:** ___________________ | _________________ | _________________
- **Customer Success:** _________________ | _________________ | _________________
- **Technical Support:** support@hipaa-attachments.com | 1-888-555-HIPAA (4472)

---

## Document Control

- **Version:** 1.0
- **Created:** [Date]
- **Last Updated:** [Date]
- **Next Review:** [Date]
- **Owner:** [Name]

---

*This checklist should be updated throughout the customer onboarding process and retained for reference and continuous improvement.*
