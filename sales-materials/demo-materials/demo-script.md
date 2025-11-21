# HIPAA Attachments Platform - Demo Script

## Pre-Demo Preparation (15 minutes before call)

### Technical Setup:
- [ ] Test screen sharing capability
- [ ] Open Azure Portal (Logic Apps workflow runs)
- [ ] Open Application Insights (monitoring dashboard)
- [ ] Have test 275 file ready (`test-x12-275-availity-to-pchp.edi`)
- [ ] Prepare ROI calculator with prospect's numbers
- [ ] Review prospect's notes (claims system, pain points, volume)

### Materials Ready:
- [ ] Prospect-specific ROI analysis
- [ ] Architecture diagram
- [ ] Integration examples for their claims system
- [ ] Case study from similar organization
- [ ] Pricing sheet
- [ ] Next steps timeline

---

## Demo Script (30 minutes total)

### Part 1: Opening and Discovery (5 minutes)

**[Join call 2 minutes early]**

**YOU:** "Hi [Name], thanks for joining! Can you see and hear me okay?"

**PROSPECT:** "Yes, all good."

**YOU:** "Perfect. I've got about 30 minutes blocked out. Before we dive in, I want to make sure we focus on what's most valuable to you."

**YOU:** "Based on our last conversation, you mentioned [pain point]. Is that still the top priority, or has anything changed?"

**PROSPECT:** [Response]

**YOU:** "Great. Here's what I'm planning to show you today:"
1. "Quick overview of the problem we solve (2 min)"
2. "Live platform demo with your specific use case (15 min)"
3. "Integration with [Their Claims System] (5 min)"
4. "ROI specific to your volume and costs (5 min)"
5. "Next steps (3 min)"

**YOU:** "Sound good? And please interrupt me anytime with questions—this is your demo."

**[Begin screen share]**

---

### Part 2: Problem Overview (2 minutes)

**YOU:** "So before I show you the platform, let me quickly frame the problem we're solving."

**[Show slide or whiteboard: Current Manual Process]**

**YOU:** "Right now, when a provider sends a 275 attachment through Availity, most payers go through this process:"

1. **Manual SFTP Login** - Staff logs into Availity SFTP daily
2. **File Download** - Downloads .edi files to local machine
3. **Manual Decode** - Opens file, reads X12 format manually or with basic tool
4. **Claim Lookup** - Searches claims system for matching claim number
5. **Data Entry** - Manually enters attachment metadata
6. **File Upload** - Uploads attachment to document management
7. **Link to Claim** - Creates association between attachment and claim

**YOU:** "This takes about 2-3 hours per attachment, and if there's any error—wrong claim number, corrupt file, missing information—it takes even longer."

**YOU:** "For an organization processing [Prospect's Volume] attachments per month, that's [Volume × 2] hours of staff time monthly, or about [Volume × 24] hours annually."

**YOU:** "Sound familiar?"

**PROSPECT:** [Response]

---

### Part 3: Platform Architecture Overview (3 minutes)

**YOU:** "Now let me show you how we automate this entire workflow."

**[Switch to architecture diagram or Azure Portal]**

**YOU:** "The platform has four main components:"

**1. SFTP Polling & Data Lake**
- "Logic App polls Availity SFTP every 15 minutes"
- "Automatically downloads new 275 files"
- "Archives raw files to Azure Data Lake with date-based partitioning"
- "Maintains 7-year retention for HIPAA compliance"

**2. X12 Processing & Integration Account**
- "Integration Account decodes X12 275 format"
- "Extracts claim number, member ID, provider NPI, attachment metadata"
- "Validates file structure and required fields"

**3. Claims System Integration**
- "REST API integration with [Their Claims System]"
- "Automatic claim lookup and validation"
- "Links attachment to claim with full audit trail"
- "Updates claim status if needed"

**4. Event Publishing & Downstream Processing**
- "Publishes event to Service Bus for downstream workflows"
- "Enables appeals processing, authorization workflows, etc."
- "Full observability through Application Insights"

**YOU:** "All of this happens automatically in about 3-5 minutes from file upload to claim linkage. No manual intervention required."

---

### Part 4: Live Demo - Processing a 275 Attachment (10 minutes)

**YOU:** "Let me show you this in action. I'm going to process a real 275 attachment file."

**[Navigate to Azure Portal → Logic Apps]**

#### Step 1: Show SFTP Trigger
**YOU:** "Here's the Logic App workflow called `ingest275`. It's configured to check Availity SFTP every 15 minutes for new files in the `/inbound/attachments` folder."

**[Show workflow definition, trigger configuration]**

**YOU:** "When it finds a new file, it automatically downloads it and kicks off the processing."

#### Step 2: Upload Test File
**YOU:** "I'm going to upload a test 275 file to our demo SFTP server to simulate Availity sending an attachment."

**[Upload test-x12-275-availity-to-pchp.edi]**

**YOU:** "In a production environment, this would be coming from Availity automatically."

#### Step 3: Show Workflow Execution
**[Navigate to Logic App → Runs History]**

**YOU:** "And here we can see the workflow running in real-time. Let me open this run..."

**[Open the active run]**

**YOU:** "You can see each step of the process:"

1. **SFTP Get File** - "Downloaded the attachment from SFTP"
2. **Archive to Data Lake** - "Stored raw file in `hipaa-attachments/raw/275/2025/01/15/`"
3. **Decode X12 275** - "Integration Account decoded the X12 message"
4. **Extract Metadata** - "Pulled out claim number: 2025010112345, member ID: ABC123456, provider NPI: 1234567890"
5. **QNXT API Call** - "Called [Their Claims System] API to validate claim and link attachment"
6. **Publish to Service Bus** - "Published event to `attachments-in` topic for downstream processing"
7. **Delete from SFTP** - "Cleaned up SFTP folder (file is safely archived in Data Lake)"

**YOU:** "The entire process took [check duration] to complete. Compare that to 2-3 hours manually."

#### Step 4: Show Application Insights
**[Navigate to Application Insights]**

**YOU:** "And here's the monitoring dashboard showing all activity in real-time."

**[Show metrics: throughput, success rate, performance]**

**YOU:** "You can see:"
- "Total attachments processed: [X]"
- "Success rate: 99.8%"
- "Average processing time: 3.2 minutes"
- "Failed transactions with detailed error logs"

**YOU:** "All of this is HIPAA compliant—PHI is automatically masked in logs, and everything is encrypted at rest and in transit."

---

### Part 5: Integration with Claims System (5 minutes)

**YOU:** "Now let me show you how this integrates with [Their Claims System]."

**[Show integration configuration or API documentation]**

**YOU:** "The platform uses your claims system's REST API to:"
1. "Validate the claim exists and is in an appropriate status"
2. "Link the attachment document to the claim"
3. "Update claim flags (e.g., 'Attachment Received')"
4. "Create audit trail entry"

**YOU:** "We've built pre-configured integrations for:"
- "QNXT (McKesson)"
- "FacetsRx (TriZetto)"
- "TriZetto QCARE"
- "Custom REST APIs"

**YOU:** "For [Their Claims System], we'll use [explain specific integration approach]. The configuration is entirely driven by a simple JSON file—no custom code required."

**[Show example configuration file]**

**YOU:** "Here's what the configuration looks like. You define:"
- "API endpoint URL"
- "Authentication method (OAuth, API key, etc.)"
- "Field mappings between X12 elements and your system's fields"
- "Business rules (e.g., only process if claim is in 'Pended' status)"

**YOU:** "This makes onboarding new payers incredibly fast—typically 3-5 days for backend integration."

---

### Part 6: Security & Compliance (3 minutes)

**YOU:** "Let me quickly touch on security since that's usually a top concern."

**[Show security architecture diagram or Key Vault]**

**YOU:** "The platform is fully HIPAA compliant with:"

**1. Premium Key Vault**
- "HSM-backed keys (FIPS 140-2 Level 2)"
- "All secrets (SFTP passwords, API keys) stored in Key Vault"
- "90-day soft delete and purge protection"
- "Audit logging for every secret access"

**2. Private Endpoints & Network Isolation**
- "VNet integration for Logic Apps"
- "Private endpoints for Storage, Service Bus, and Key Vault"
- "Zero public internet exposure for PHI data"

**3. PHI Masking in Logs**
- "Application Insights automatically masks member IDs, SSNs, claim numbers"
- "Real-time monitoring for unmasked PHI"
- "Compliance alerts for policy violations"

**4. Data Lifecycle Management**
- "Automated tier transitions (Hot → Cool → Archive)"
- "7-year retention for HIPAA compliance"
- "Secure deletion after retention period"

**YOU:** "We're also SOC 2 Type II certified and working on HITRUST certification."

---

### Part 7: ROI Analysis (5 minutes)

**YOU:** "Now let's talk about the return on investment specific to your organization."

**[Share ROI calculator spreadsheet]**

**YOU:** "Based on what you told me, you're processing [Volume] attachments per month. Let's be conservative and say 1.5 hours per attachment on average."

**[Enter numbers into calculator]**

**Current State:**
- "Monthly attachments: [Volume]"
- "Hours per attachment: 1.5"
- "Monthly staff hours: [Volume × 1.5]"
- "Hourly cost: $50 (blended rate)"
- "Monthly cost: $[Volume × 1.5 × 50]"
- "Annual cost: $[Volume × 18 × 50]"

**With Platform:**
- "Automated processing time: 0.1 hours (6 minutes oversight)"
- "Monthly staff hours: [Volume × 0.1]"
- "Monthly cost: $[Volume × 0.1 × 50]"
- "Annual cost: $[Volume × 1.2 × 50]"

**Savings:**
- "Annual savings: $[Difference]"
- "Platform cost: $10,000/year + $15,000 implementation (first year)"
- "First-year ROI: [Savings / 25,000]x"
- "Ongoing ROI: [Savings / 10,000]x per year"

**YOU:** "And this doesn't even include:"
- "Reduced errors and rework"
- "Faster claim adjudication"
- "Reduced provider call volume"
- "Better compliance posture"

**YOU:** "We guarantee this ROI. If you don't save at least $25,000 in the first year, we'll refund your investment. How does that sound?"

**PROSPECT:** [Response]

---

### Part 8: Next Steps (3 minutes)

**YOU:** "So based on what you've seen, what do you think?"

**PROSPECT:** [Response]

**[If positive]**

**YOU:** "Great! Here's what I suggest for next steps:"

**1. Technical Architecture Review (This Week)**
- "30-minute call with your IT team"
- "Review Azure infrastructure, security, and integration approach"
- "Answer technical questions"

**2. Formal Proposal (Next Week)**
- "Detailed pricing based on your volume"
- "ROI analysis and business case"
- "Implementation timeline"

**3. Contract Review (Following Week)**
- "Master Services Agreement and SOW"
- "Security and compliance exhibits"
- "Legal review and negotiation if needed"

**4. Implementation Kickoff (3 weeks)**
- "90-day implementation plan"
- "Discovery and requirements gathering"
- "Go-live target: [Date 90 days out]"

**YOU:** "Does this timeline work for you? Any concerns or questions?"

**[If hesitant]**

**YOU:** "What would help you move forward? Is there anything I didn't cover, or do you need to involve anyone else in the decision?"

**PROSPECT:** [Response]

**YOU:** "Understood. Let me [address concern]. I'll also send you [relevant materials]. Can we schedule a follow-up call for [Date] to discuss further?"

---

### Part 9: Q&A and Wrap-Up (5 minutes)

**YOU:** "Before we wrap up, what questions do you have for me?"

**[Answer questions]**

**Common Questions:**

**Q: "How long does implementation take?"**
**A:** "90 days from contract signature to go-live. Broken down into: 2 weeks discovery, 6 weeks development, 3 weeks testing, 1 week training and deployment."

**Q: "What claims systems do you support?"**
**A:** "We support any system with a REST API. We have pre-built connectors for QNXT, FacetsRx, and TriZetto. Custom integrations take 3-5 days."

**Q: "Can we start with a pilot?"**
**A:** "Absolutely. We typically pilot with 100-200 attachments over 30-60 days before full rollout."

**Q: "What happens if Availity changes their SFTP structure?"**
**A:** "We monitor Availity announcements and update the platform proactively. Updates are included in your annual license."

**Q: "How do we handle multiple payers?"**
**A:** "The platform supports multiple payers through configuration. Additional payers are $7,000-10,000/year depending on volume."

**Q: "What if we want to move to a different claims system?"**
**A:** "No problem—it's just a configuration change. Takes 1-2 days to set up the new integration."

---

## Post-Demo Actions (Immediately After Call)

### Within 1 Hour:
- [ ] Send thank-you email with recap
- [ ] Attach demo recording (if recorded)
- [ ] Include architecture diagram and case study
- [ ] Schedule technical call (if requested)
- [ ] Log notes and next steps in CRM

### Within 24 Hours:
- [ ] Prepare and send formal proposal
- [ ] Create custom ROI analysis spreadsheet
- [ ] Draft SOW and pricing sheet
- [ ] Coordinate with technical team for architecture review

### Follow-Up Schedule:
- **Day 1:** Proposal sent email
- **Day 3:** Follow-up call to discuss proposal
- **Day 7:** Technical architecture call (if needed)
- **Day 10:** Contract discussion
- **Day 14:** Final decision follow-up

---

## Demo Variations

### Short Demo (15 minutes)
- Skip architecture deep-dive
- Focus on workflow execution and ROI
- Use for busy executives

### Technical Demo (45 minutes)
- Include Bicep infrastructure review
- Show Logic App workflow JSON
- Review API integration code
- Discuss security architecture in depth
- Use for IT/engineering teams

### Executive Demo (20 minutes)
- Focus on business value and ROI
- Show high-level architecture only
- Emphasize risk reduction and compliance
- Include competitive comparison
- Use for C-level or VP-level

---

## Demo Best Practices

### Do's:
✅ Personalize with prospect's specific data
✅ Use their claims system in examples
✅ Address their stated pain points
✅ Keep it conversational and interactive
✅ Pause for questions frequently
✅ Show real workflow runs, not slides
✅ Demonstrate ROI with their numbers
✅ Record the session (with permission)

### Don'ts:
❌ Use generic "Company XYZ" examples
❌ Talk for more than 2 minutes without pausing
❌ Dive too deep into technical details upfront
❌ Skip the ROI discussion
❌ Rush through security and compliance
❌ Forget to schedule next steps before ending

---

## Common Demo Mistakes to Avoid

1. **Technical Overload:** Don't show code unless they ask. Focus on business value.

2. **Forgetting to Qualify:** Confirm their volume and pain points at the start.

3. **No Clear Next Steps:** Always end with scheduled follow-up.

4. **Ignoring Questions:** Stop and address concerns immediately.

5. **Weak Close:** Ask for the sale/commitment at the end.

6. **No ROI:** Don't skip the financial justification.

7. **Static Demo:** Show live workflow runs, not just screenshots.

8. **One-Size-Fits-All:** Customize for each prospect's situation.

---

## Demo Metrics to Track

### Conversion Rates:
- **Demo to Proposal:** 60-80% (target)
- **Demo to Closed-Won:** 30-40% (target)
- **Average Deal Size:** $25,000 first year

### Demo Quality Indicators:
- **Questions Asked:** 5-10 (good engagement)
- **Demo Duration:** 25-35 minutes (not too short, not too long)
- **Technical Call Requested:** 40-60% (indicates serious interest)
- **Next Meeting Scheduled:** 80%+ (critical success metric)

### Post-Demo Follow-Up:
- **Proposal Sent:** Within 24 hours
- **Follow-Up Call:** Within 3 days
- **Decision Timeline:** 2-4 weeks average

---

## Additional Demo Resources

### Demo Environment Checklist:
- [ ] Azure subscription with Logic Apps deployed
- [ ] Test SFTP server with sample 275 files
- [ ] Application Insights with demo data
- [ ] Sample integration to mock claims system
- [ ] Architecture diagrams updated
- [ ] ROI calculator with formulas validated

### Demo Materials to Share:
- Architecture diagram (PDF)
- Security & compliance overview (PDF)
- Case study from similar organization
- ROI calculator (Excel)
- Technical FAQ
- Implementation timeline

### Demo Recording and Distribution:
- Record all demos (with permission)
- Upload to shared drive
- Send recording to prospect within 2 hours
- Include timestamp links to key sections
- Add to internal demo library for training
