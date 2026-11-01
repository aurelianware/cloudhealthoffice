# Security incident response and breach notification: hosted environment

**Status:** DRAFT, not adopted. Counsel reviews it before adoption.
**Rule:** 45 CFR 164.308(a)(6) security incident procedures; 164.410 notification by a business associate.

## 1. What counts

- **Security incident.** An attempted or successful unauthorized access, use, disclosure, change or destruction of information, or interference with the system. Examples:
  - an alert on an IAM or key change nobody planned
  - an unknown sign-in to the portal
  - a lost laptop
  - ePHI found in a log, ticket or chat
  - a misdirected file
  - malware
- **Breach.** An acquisition, access, use or disclosure of unsecured PHI that HIPAA doesn't permit, unless a documented risk assessment shows a low probability that the PHI was compromised (164.402). PHI encrypted with keys that weren't exposed is not "unsecured".

Routine unsuccessful attempts (port scans, blocked WAF requests, failed sign-ins without other signs) are security incidents too. They are logged and reported to plans in aggregate as each BAA allows, without individual notice.

## 2. Roles

| Role | Who |
| --- | --- |
| Incident lead | Security Officer (or backup) |
| Privacy lead | Privacy Officer: breach assessment and plan notice |
| Engineering | Whoever the incident lead assigns |
| Counsel | Outside counsel, for any suspected breach |

Anyone in the workforce who notices a possible incident tells the Security Officer at once. Use security@cloudhealthoffice.com and a phone call if it's urgent. Never use a public GitHub issue, and never paste ePHI into the report.

## 3. Steps

| Step | Target time | What happens |
| --- | --- | --- |
| 1. Report | Immediately | Report what was seen, when, and where. Don't investigate alone or delete anything |
| 2. Triage | Within 4 hours of report | The incident lead opens an entry in the incident log (section 6), rates severity (S1: ePHI possibly exposed or the service down; S2: a control failed without known exposure; S3: other), and starts the clock |
| 3. Contain | As soon as possible | Disable accounts or keys, revoke sessions (IAP, CHO tokens), block addresses in Cloud Armor, isolate workloads, pause the CPCDS drop. Preserve evidence first: export the relevant audit logs to the compliance store before changing anything that would overwrite them |
| 4. Investigate | Ongoing | Use audit logs, application audit events and Kubernetes events to establish what ePHI, which members, which plan, what period, and who |
| 5. Assess | Within 5 business days | The privacy lead runs the four-factor risk assessment (164.402(2)): the nature and extent of the PHI, who received it, whether it was actually acquired or viewed, and how far the risk was mitigated. Record the conclusion: breach or not |
| 6. Notify the plan | See section 4 | Send the written notice |
| 7. Recover | After containment | Restore from backup if needed ([CONTINGENCY-PLAN.md](CONTINGENCY-PLAN.md)), rotate credentials, redeploy |
| 8. Review | Within 30 days of close | Root cause, what worked, what changes. Update the risk analysis and these procedures |

## 4. Notifying the plan

- **Deadline.** Without unreasonable delay, and no later than the BAA's deadline from discovery. The [BAA template](../../diligence/BAA-TEMPLATE.md) says fifteen (15) calendar days. HIPAA's outer limit is 60 days (164.410(b)), but the shorter contract term wins. Discovery is the first day the breach is known to anyone in the workforce, or would have been known with reasonable diligence.
- **For S1 incidents,** give the plan's named compliance contact an early heads-up by phone or email within 2 business days, even before the assessment ends, unless law enforcement asks for a delay (164.412).
- **The written notice includes (164.410(c)):**
  - each member affected, or the best list available so far
  - what happened, the date of the breach and the date of discovery
  - what kinds of PHI were involved
  - what Aurelianware is doing to investigate, mitigate and prevent a repeat
  - a contact for questions
  - anything else the plan needs for its own notices to members, HHS and the media
- **Follow up** as more information becomes available.
- **The plan, as covered entity, notifies members, HHS and the media** unless its BAA delegates that to Aurelianware. Aurelianware pays for and carries out any notices delegated to it.

## 5. Severity and contacts

| Contact | Details |
| --- | --- |
| Security Officer | ________ (phone ________) |
| Privacy Officer | ________ (phone ________) |
| Counsel | ________ |
| Google Cloud support | Console, Support (case priority P1 for production outage) |
| Each plan's compliance contact | Order Form for that plan |

## 6. Incident log

Keep one record per incident in the private compliance store, kept six years:

| Field | |
| --- | --- |
| Id and title | |
| Reported by, at (date and time) | |
| Discovered at | |
| Severity | |
| Systems and plan(s) | |
| ePHI involved? (yes, no, unknown) | |
| Containment actions and times | |
| Risk assessment conclusion and reasoning | |
| Plan notified (date, by whom, how) | |
| Root cause | |
| Follow-up changes and owners | |
| Closed at | |

## 7. Exercise

Once a year, the Security Officer runs a tabletop exercise with everyone who has access. Suggested scenarios are a stolen Aurelianware laptop and an unexpected IAM grant alert at night. Record it in the incident log as "exercise".
