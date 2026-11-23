# BAA-ready checklist

**Status as of:** November 23, 2026. Nothing below is done yet unless it says so.
**Goal:** when a plan sends its BAA, Aurelianware can sign it and honor it. The contract is the easy part. A plan's security team checks that we can back it up, and this is the list of what they check.

Tick each item and record the date in the private compliance store, not in this public file. Items marked **Officer** need the account owner (the CTO) or a company officer and can't be delegated to code or to Claude.

## 1. Vendor BAAs (do now)

Details: [VENDOR-BAA-REGISTER.md](../hipaa-hosted/VENDOR-BAA-REGISTER.md).

- [ ] **Officer:** accept the Google Workspace BAA in the Admin console (Account, Legal and compliance). Free, minutes. It backs up the "no PHI in email" rule and covers Drive as the compliance store.
- [ ] **Officer:** create the Google Cloud organization and the hosted project, then accept the Google Cloud HIPAA BAA for it (IAM & Admin, Privacy). Free. Steps 1 to 3 of [HOSTED-ENVIRONMENT-APPLY.md](HOSTED-ENVIRONMENT-APPLY.md). Applying the infrastructure, which costs money, can wait.
- [ ] Confirm every Google Cloud service the hosted profile uses is on Google's covered products list (the table in the register).
- [ ] Keep these out of the PHI path, as the register says: GitHub, the demo cluster, Cloudflare, outside observability tools, chat. No change needed today; it's the rule to keep.
- [ ] Anthropic BAA only if a plan wants the AI claims examiner. Until then it stays off in the hosted deploy.

## 2. People and policies (do now, a few weeks)

All drafts are in [hipaa-hosted/](../hipaa-hosted/README.md).

- [ ] **Officer (CEO and CTO):** sign the [Security Officer and Privacy Officer designation](../hipaa-hosted/SECURITY-OFFICER-DESIGNATION.md).
- [ ] **Officer:** engage counsel to review the policies, the incident procedure, the BAA template and its Medicaid rider in one pass ($5K to $15K).
- [ ] Complete the [risk analysis](../hipaa-hosted/RISK-ANALYSIS.md) with the real project id, region, people and vendors; give each open item an owner and a date.
- [ ] **Officer (CEO and CTO):** sign and date the [security policies](../hipaa-hosted/POLICIES.md), the risk analysis, the [incident procedure](../hipaa-hosted/INCIDENT-RESPONSE.md) and the [contingency plan](../hipaa-hosted/CONTINGENCY-PLAN.md). Adopted copies go to the compliance store.
- [ ] Every workforce member (today the two founders) completes the [training](../hipaa-hosted/TRAINING-RECORD.md), the device checklist and the signed acknowledgement.
- [ ] **Officer:** turn on 2-step verification with a security key or authenticator app for every company Google account, and enforce it in the Workspace Admin console.
- [ ] **Officer:** set up the private compliance store: a Drive folder shared only with the Security Officer and the CEO.
- [ ] Adopt a background check policy and run checks for anyone who will have access to the hosted environment (from the second engineer on).
- [ ] Set up `security@` and `privacy@` groups, and a phone that receives security alerts (needed for a one-hour notice clock).

## 3. Breach notification (do now)

- [ ] Counsel confirms the timelines table in the [incident procedure](../hipaa-hosted/INCIDENT-RESPONSE.md), section 4. HIPAA's outer limit to the plan is 60 days, our template promises 15, and a Texas Medicaid plan's flow-down asks for 1 to 24 hours.
- [ ] Fill in the contacts table (section 5): officers' phones, counsel, and the insurer's breach hotline once bound.
- [ ] Run one tabletop exercise before first PHI (a stolen laptop, and a night-time IAM alert), and record it.

## 4. Cyber insurance (before first PHI)

Plans commonly require $1M to $5M of cover in the contract, and ask for a certificate during the security review. Expect about $3K to $10K a year to start; limits, and the premium, rise when a pilot signs.

- [ ] **Officer:** get quotes from two or three brokers or carriers that write for small software companies (for example Vouch, Embroker, Coalition or At-Bay).
- [ ] Ask for both parts, often sold together:
    - **Cyber liability:** breach response (forensics, counsel, notification, credit monitoring), privacy liability to third parties, regulatory defense and fines where insurable, business interruption, ransomware.
    - **Technology errors and omissions:** claims that the software or service failed. Plans often require this from a software vendor alongside cyber.
- [ ] Start at $1M, with the option to raise to $2M to $5M when a plan's contract asks.
- [ ] Ask that a plan can be named as additional insured, and that the policy has no exclusion for HIPAA or regulatory matters.
- [ ] Expect the application to ask about MFA, backups, encryption and incident response. The answers come from this checklist, so finish sections 1 and 2 first for a better price.
- [ ] Put the carrier's breach hotline in the incident procedure.

## 5. Technical (when a plan starts its security review)

Details: [HOSTED-ENVIRONMENT-APPLY.md](HOSTED-ENVIRONMENT-APPLY.md).

- [ ] Apply the hosted profile; deploy with a synthetic test tenant.
- [ ] Restore drill passes, timed and recorded.
- [ ] WAF tuned for two weeks, then enforced.
- [ ] Penetration test of the hosted endpoints against a synthetic tenant.
- [ ] Code fixes in their own pull requests: portal idle timeout down from 2 hours; alerts on failed sign-ins in CHO; hosted Terraform state in a bucket.
- [ ] Sample application logs to confirm they carry no PHI.

Already in place in code (built, not yet running): encryption at rest with customer-managed keys and in transit, role-based access with a permission on every endpoint, tenant isolation, a record access audit log, six-year cloud audit logs, daily locked backups and security alerts. See [HIPAA-POSTURE.md](HIPAA-POSTURE.md).

## 6. SOC 2 (in parallel)

Not needed to sign a BAA, but plans expect at least a start. See [SOC2-ROADMAP.md](SOC2-ROADMAP.md).

- [ ] **Officer:** pick a compliance platform (trial two in the same week).
- [ ] Load the adopted policies into it and connect Workspace, GitHub and, once it exists, the hosted project.
- [ ] Type I about three months after the hosted environment is applied.

## 7. When a plan sends its BAA

- [ ] Ask in the first security call for the plan's BAA, its security questionnaire, and any state flow-down (for Texas Medicaid, its HHS Data Use Agreement subcontractor terms).
- [ ] Counsel compares the plan's paper with [BAA-APPROACH.md](BAA-APPROACH.md): notice clocks we can meet, capped liability, reasonable audit rights, US-only, Google named as subcontractor.
- [ ] Answer the questionnaire from [PAYER-SECURITY-QUESTIONNAIRE.md](PAYER-SECURITY-QUESTIONNAIRE.md), updating every answer this checklist has changed.
- [ ] Send the adopted policies, the risk analysis summary, the insurance certificate and the penetration test letter under NDA.
- [ ] **Officer:** sign the BAA and the flow-down. Record the plan's notice deadlines and compliance contact in the Privacy Officer's list.
- [ ] Then onboard the plan: [HOSTED-ENVIRONMENT-APPLY.md](HOSTED-ENVIRONMENT-APPLY.md), "After a plan signs".

## Only the account owner or an officer can do these

| Item | Section | Cost |
| --- | --- | --- |
| Accept the Google Workspace BAA | 1 | Free |
| Create the Google Cloud organization and hosted project; accept the Google Cloud BAA | 1 | Free until the infrastructure is applied |
| Sign the officer designation, the policies and the risk analysis | 2 | Free |
| Engage counsel | 2 | $5K to $15K |
| Enforce 2-step verification; set up the compliance store and the security and privacy groups | 2 | Security keys about $50 each |
| Bind cyber and E&O insurance | 4 | About $3K to $10K a year to start |
| Pick and pay for the compliance platform | 6 | $10K to $25K a year |
| Sign each plan's BAA | 7 | Free |
