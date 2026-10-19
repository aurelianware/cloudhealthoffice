# CHO Service: payer-native call tracking and member/provider service

**Status:** Draft for review (2026-10-15)
**Scope:** A customer-service module for health plan call centers:
- call tracking;
- classification of every contact as inquiry, grievance, coverage request or appeal;
- follow-up work;
- audit-ready reporting;
- AI wrap-up.

It runs inside CHO, or standalone beside another core admin system such as QNXT.

---

## 1. Why build it

- **QNXT plans need a replacement.** QNXT's call tracking is being retired. That's known from inside QNXT plans; Cognizant hasn't announced it publicly, but the signs fit: Cognizant presents call tracking only inside myQNXT, the QNXT product page no longer lists it, and its new investment (TriZetto Unify AI agents) starts with prior authorization, not service. Plans are shopping now.
- **The alternatives are general CRMs plus a long integration project:**
  - **Salesforce (Health Cloud / Agentforce Health):** one consultant estimates $250–500 per user per month on top of Service Cloud, and 4–9 months for a mid-size project. Integration is 30–40% of the cost.
  - **Microsoft Dynamics 365:** payer projects run as multi-year, integrator-led roadmaps, wired into Facets, IVR and care management one at a time.
  - **Pega:** strong on guided workflows; no published detail on how it integrates with core admin systems.
  - **Zipari:** the closest to a payer-specific call-center product, recently acquired by mPulse.

  None of these knows payer data natively: each has to be taught what a claim, authorization or grievance is.
- **The compliance work isn't built in.** Classifying contacts correctly, running regulatory clocks and producing audit reports are left as configuration in the generic tools. That's where plans get cited.
- **Plans don't want lock-in.** A plan that resisted having a service vendor wired into its clearinghouse integration did so because switching later would have meant redoing that integration. The module has to sit **beside** a plan's integrations, never inside them.

## 2. Principles

1. **Payer-native.** Members, eligibility, claims, authorizations, appeals, providers and accumulators are first-class, not custom objects.
2. **Standalone or built in.** In CHO it reads CHO directly. Beside QNXT (or another core system) it reads through adapters, the same pattern CHO uses for claims and benefit plans. It never writes to the core system.
3. **Removable.** The module touches nothing in a plan's clearinghouse, portal or core integrations. Removing it leaves them unchanged. All data can be exported, with open APIs and FHIR where a standard exists.
4. **Classification is mandatory.** No contact closes without a classification, and the module helps the rep get it right.
5. **One work engine.** Follow-ups go through work management (queues, Get Next, due dates), the same engine as claims and appeals.

## 3. The contact record

```
Contact
  id, tenantId, channel (Phone|IVR|Chat|Email|Portal|Walk-in|Mail), direction (Inbound|Outbound)
  caller        { type: Member|Representative|Provider|Other, name, phone, npi?, relationship?, aorOnFile? }
  member?       memberId (verified: how — DOB + ID, etc.)
  provider?     npi, name
  startedAt, endedAt, agentId, queue, phoneCallId? (CTI correlation)
  reasons[]     reason codes (tenant list: Eligibility, Benefits, Claim status, Auth status, ID card, PCP change, Billing, Complaint, …)
  subjects[]    links: Claim|Authorization|Appeal|Grievance|PaymentRun|Document — each claim discussed gets a note
  classification  Inquiry | Grievance | ExpeditedGrievance | CoverageRequest | Appeal | ExpeditedAppeal
                  + classifiedBy, classifiedAt, aiSuggestion?, overrideReason?
  resolution    ResolvedOnCall | FollowUpCreated | Transferred | CallbackScheduled
  followUps[]   work item ids
  summary       rep text (+ AI draft, accepted/edited)
  recording?, transcript?   documents in the document store
```

**Notes on claims:** every claim or authorization linked to a contact gets a "Member contact" or "Provider contact" note, through the claim notes shipped in #1355. The claim history then shows the call without anyone retyping it.

## 4. Classification and regulatory clocks

The classification decides what happens next:

| Classification | Effect |
|---|---|
| Inquiry | Resolved on the call, or a follow-up work item with the tenant's service-level due date. |
| Grievance | Opens a grievance case with the **received date = the call time**. |
| Expedited grievance | As above, with a 24-hour clock. |
| Coverage request (organization determination) | Opens an authorization or pre-service determination request, dated from the call. |
| Appeal / expedited appeal | Opens an appeal in appeals-service, dated from the call. |

**Medicare Advantage** (42 CFR 422.564):
- Grievances may be filed orally or in writing, within 60 days of the event.
- They must be resolved within 30 days, with up to a 14-day extension when the enrollee asks for it or when the plan needs more information and documents that the delay is in the enrollee's interest.
- Two kinds of grievance need a 24-hour response: one about the plan invoking an extension on a determination or reconsideration, and one about the plan refusing to expedite.
- The plan must "promptly determine and inform the enrollee" whether a complaint is a grievance or an appeal.
- Records must keep at least the date received, the final disposition, and the date the enrollee was notified.

**Medicaid managed care and state rules:** the full set of levels, timeframes and audit requirements, verified against the regulations, is in [appeals-grievances-compliance.md](appeals-grievances-compliance.md). In short: one level of member appeal, decided in 30 days (72 hours expedited); grievances resolved within 90 days; then a State fair hearing, plus external medical review where the state offers it.

**Configuration:** the rules live in dated rule packs per line of business and state ([appeals-grievances-compliance.md](appeals-grievances-compliance.md) §3), like the prompt-pay interest rules in [claim-review-workbench.md](claim-review-workbench.md) §3.3.

**How the module helps the rep classify correctly:**
- The classification step can't be skipped.
- **AI flagging:** Claude reads the transcript or the rep's notes and flags complaint or dissatisfaction language ("I want to file a complaint", "this is unacceptable", "I was told I can't get…"), plus requests for a coverage decision or a re-review. When the rep picks *Inquiry* on a flagged contact, they must give a reason, and the case goes on a supervisor review list.
- **Supervisor sampling:** a daily random sample of contacts closed as *Inquiry* goes to quality review. That's the control auditors look for.

## 5. Audit-ready reporting

Produced from contacts and the cases they open:
- **Grievance case list:** every grievance with received date, classification, expedited flag, disposition, resolution and notification dates, and timeliness.
- **Coverage request and appeal case lists,** dated from the call where it started there.
- **Misclassification review:** AI-flagged contacts closed as inquiries, with the supervisor's outcome.
- **Operating metrics:** volume and reason trends, first-call resolution, handle time and after-call work, follow-ups overdue, and repeat callers.

The export layouts are versioned CMS program audit templates; see [appeals-grievances-compliance.md](appeals-grievances-compliance.md) §5.

## 6. Working the call

1. **Screen pop.** The phone platform sends the caller's number and IVR inputs (member ID, NPI). CHO opens the caller's record already matched when it can, or a search when it can't. Connectors: Amazon Connect, Genesys Cloud, Five9 and Twilio (webhook plus CTI events). No phone platform? The rep starts a contact by hand.
2. **360 view.** Member view: eligibility and plan, PCP, accumulators, recent claims with plain-language status (the same line-level reasons shipped in #1354), authorizations, appeals, documents, prior contacts, and open follow-ups. Provider view: claims, payments, open RFAIs and authorizations.
3. **During the call:** the rep links claims and authorizations as they're discussed and picks reasons. Common actions are one click: reprint ID card, send EOB copy, open a reprocessing request, schedule a callback.
4. **Wrap-up:** Claude drafts the summary and a suggested classification and reasons from the transcript or notes. The rep edits and accepts. A person makes the final classification every time.
5. **Follow-ups:** each one becomes a work item (WorkType `Inquiry`, or the case type) with a due date, routed by queue membership and worked through Get Next.

### 6.1 Letters and notices

How the module works with [correspondence.md](correspondence.md) (details in its §9.1):

- **One-click letter actions are correspondence requests.** Reprint ID card, send EOB copy and resend a letter go through the correspondence service with the contact as their source, so they get the same templates, delivery and proof as any other letter. A lost-card request appears in the member's ID card request history ([correspondence.md](correspondence.md) §10.1).
- **Cases opened from a call get their letters from the rule pack.** A grievance or appeal classified on the call gets its acknowledgment, extension and resolution notices automatically.
- **Outbound calls can be notices.** An outbound contact classified as a notice records `OralNoticeGiven` on the case, with the time and whether it was a conversation or a voicemail. That covers MA expedited decisions, Medicaid extension notices and oral responses to oral MA grievances; the written confirmation's clock starts from the call. Failed attempts are recorded, and the written notice goes out on time regardless.
- **Correspondence panel in the 360 view:** every letter sent to the member or provider, with its letter date, the date it left the plan, USPS events, returns and portal views. The rep answers "I never got my letter" from the panel and can reissue in one action.
- **Structured capture on the contact screen:** language and accessible-format preferences, address changes, and electronic delivery consent (for Medicare Advantage, consent must name the medium and the letter types), each recorded with the contact as its source.
- **Letter problems become outbound calls.** Returned mail and failed electronic delivery can create outbound-call work items in the same queues.

### 6.2 Call routing: integrate with the contact-center platform

**CHO doesn't route calls.** Telephony, IVR, queues, routing, recording and workforce management belong to the plan's contact-center platform (Amazon Connect, Genesys Cloud, Five9, NICE CXone, Twilio Flex). Plans already have one, it's a commodity, and building it would break the removable, beside-the-core principle (§2). CHO gives the router the payer knowledge it doesn't have.

| Piece | What CHO provides | How the platform uses it |
|---|---|---|
| **Routing context API** | From the caller's number and IVR inputs (member ID, NPI, date of birth): the matched member or provider and routing attributes: line of business, language preference, open expedited appeal or grievance, an open case near its deadline, a recently returned letter, an outbound callback the caller is returning | Picks the queue and priority in its call flow (Amazon Connect Lambda, Genesys data action, Five9 or Twilio webhook). Example: a member with an expedited appeal due in 6 hours goes to the front of the appeals queue in Spanish. |
| **IVR self-service** | Endpoints for ID card request (lands in the ID card request history, [correspondence.md](correspondence.md) §10.1), claim status, eligibility, PCP lookup | Callers finish simple requests without a rep. Each is logged as a contact with channel `IVR`, so reporting and audit cover self-service. |
| **Screen pop** | The matched record and open items (§6 step 1) | Opens the CHO contact screen in the agent desktop. |
| **Outbound calls** | Click-to-dial from a work item (oral notice due, returned-mail address check, callback) | Places the call; reports the outcome (connected, voicemail, no answer, wrong number) back to CHO, which records it on the contact and, for notices, records `OralNoticeGiven` ([correspondence.md](correspondence.md) §9.1). |
| **Recordings and transcripts** | Intake into the document store, linked to the contact | Sends them by webhook when the call ends (§9, open question 2). |

**Rules for the integration:**
- **Attributes, not records.** The routing API returns what the router needs to decide (flags, codes, a priority), never clinical detail or claim content. IVR prompts speak only what self-service needs after the caller is authenticated.
- **IVR authentication** follows the same verification rules as a rep (member ID plus date of birth, or the plan's policy), recorded on the contact.
- **One adapter per platform** behind a shared interface, the same pattern as CHO's other adapters. Google first (decided 2026-10-17, see below).
- **Compliance:** a BAA with the platform vendor; call-recording consent where state law requires all parties to consent; outbound calling rules for prerecorded or autodialed calls, which is why notices use rep-initiated click-to-dial; disclosure limits when a call reaches voicemail. *The specific requirements are to confirm before building.*

**Platform order (decided 2026-10-17): Google first.** CHO's demo already runs on GKE in Google Cloud, so the IVR's webhooks call CHO inside the same environment, IAM and BAA. Google's HIPAA covered products include Google Cloud Contact Center as a Service, Agent Assist for CCaaS, Conversational Agents, Speech-to-Text and Customer Experience Insights (no PHI in agent definitions; no Speech-to-Text data logging under a BAA).
1. **IVR self-service and routing context on Conversational Agents (Dialogflow CX),** calling CHO's self-service and routing-context endpoints through webhooks. A virtual agent built this way can also sit in front of other contact-center platforms.
2. **Routing, agent desktop, screen pop, recording and outbound on Google Cloud Contact Center as a Service** if procurement is quick (pricing goes through Google sales); otherwise **Amazon Connect** (pay-as-you-go) for this half. The CHO side is identical either way.
3. **A second platform adapter before a sales push,** matched to what pilot plans run; Genesys, NICE, Five9 and Amazon Connect are likely more common in payer call centers than Google (*share not verified*).

**Who distributes back-office work:** CHO's work management (Get Next) owns follow-ups, deadline work and returned mail, and remains the owner of record and deadlines. For plans that want one agent desktop, an optional adapter pushes CHO work items into the platform's task routing (Amazon Connect Tasks, Genesys work items) and syncs their completion back. See §9, open question 5.

## 7. Standalone beside QNXT

- **Read adapters:** members/eligibility, claims and status, authorizations, providers and payments, through the core system's APIs or a read replica. Nothing is written back to the core system.
- **What CHO Service owns:** contacts, classification, follow-ups, notes, documents and reporting live in CHO Service itself. For a CHO plan they're simply part of CHO.
- **Cases:** grievances and appeals open in CHO's appeals service. Alternatively, an outbound event or API hands them to the plan's existing appeals system.
- **Identity:** the plan's identity provider through OIDC (Entra ID, Okta).

## 8. Build order

1. **Contact record and the contact screen:** manual start, member/provider search, 360 view, reasons, linked subjects, notes on claims.
2. **Classification:** mandatory step, case creation for grievances and appeals dated from the call, rule packs for MA and Medicaid.
3. **Follow-ups through work management:** a new `Inquiry` work type, plus appeals as a work source.
4. **Reporting:** the grievance, coverage-request and appeal case lists, plus operating metrics.
5. **AI wrap-up:** summary, suggested classification, complaint-language flag, supervisor sampling.
6. **Phone integration** (§6.2), Google first: IVR self-service and the routing context API on Conversational Agents; then screen pop, recording and transcript intake, and outbound click-to-dial with outcomes on Google CCaaS (or Amazon Connect); then a second platform adapter matched to pilot plans.
7. **Standalone adapters for QNXT.**
8. **Correspondence links** (§6.1), as the matching correspondence steps land: letter actions and the correspondence panel with the portal inbox and first mail channel; oral notice from outbound contacts with the appeal notices; consent capture with electronic delivery.

## 9. Open questions

1. **Which phone platform first?** *Decided 2026-10-17:* Google (Conversational Agents for IVR, then Google CCaaS or Amazon Connect for routing and the agent desktop); see §6.2. Still open: which platform is second, based on what pilot plans run.
2. **Recordings:** store full recordings in the document store, or only transcripts plus a link to the phone platform's recording? Recordings are large and sensitive.
3. **Who adds contacts and notes:** the `claims:note` permission from the workbench decisions covers notes; contacts need their own (`service:contact`).
4. **Pricing and packaging:** per seat, per member per month, or included in CHO? This decides how the standalone version is positioned.
5. **Work distribution:** should CHO work items also flow into the contact-center platform's task routing for plans that want one agent desktop, and if so which platforms first? CHO stays the owner of record and deadlines either way.

## Sources

- [Google Cloud Contact Center as a Service](https://cloud.google.com/solutions/contact-center-as-a-service)
- [Google Cloud: HIPAA compliance and covered products](https://docs.cloud.google.com/docs/security/compliance/hipaa)
- [42 CFR 422.564: MA grievance procedures](https://www.law.cornell.edu/cfr/text/42/422.564)
- [Cognizant: Network Health call center on myQNXT (case study)](https://www.cognizant.com/en_us/case-studies/documents/3879600_network-health-five-star-call-center-member-experience-fueled-by-qnxt_printable.pdf)
- [Cognizant: QNXT product page](https://www.cognizant.com/us/en/industries/healthcare-technology-solutions/trizetto/core-administration/qnxt)
- [Cognizant opens TriZetto Unify to AI agents (May 2026)](https://news.cognizant.com/2026-05-29-Faster-decisions,-faster-care-for-patients-Cognizant-opens-TriZetto-Unify-to-AI-agents)
- [Salesforce Health Cloud implementation costs (consultant estimate)](https://dev.to/christopher_gainus_84f002/salesforce-health-cloud-implementation-real-costs-and-timelines-2026-48kb)
- [Hitachi Solutions: health plan customer care transformation](https://global.hitachi-solutions.com/wp-content/uploads/2024/01/Health-Plan-Customer-Care-Transformation.pdf)
- [Pega Customer Service for Healthcare](https://www.pega.com/insights/resources/pega-customer-service-healthcare)
- [Zipari platform](https://zipari.com/platform)
- [Uniphore: after-call work automation case study](https://uniphore.com/case-studies/leading-us-health-insurance-company-increases-its-agents-productivity-by-automating-mandatory-tasks-with-uniphores-u-assist)
