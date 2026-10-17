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
  id, tenantId, channel (Phone|Chat|Email|Portal|Walk-in|Mail), direction (Inbound|Outbound)
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
6. **Phone integration:** screen pop and recording/transcript intake (Amazon Connect first).
7. **Standalone adapters for QNXT.**
8. **Correspondence links** (§6.1), as the matching correspondence steps land: letter actions and the correspondence panel with the portal inbox and first mail channel; oral notice from outbound contacts with the appeal notices; consent capture with electronic delivery.

## 9. Open questions

1. **Which phone platform first?** Amazon Connect is the easiest to demo; Genesys is common in plan call centers.
2. **Recordings:** store full recordings in the document store, or only transcripts plus a link to the phone platform's recording? Recordings are large and sensitive.
3. **Who adds contacts and notes:** the `claims:note` permission from the workbench decisions covers notes; contacts need their own (`service:contact`).
4. **Pricing and packaging:** per seat, per member per month, or included in CHO? This decides how the standalone version is positioned.

## Sources

- [42 CFR 422.564: MA grievance procedures](https://www.law.cornell.edu/cfr/text/42/422.564)
- [Cognizant: Network Health call center on myQNXT (case study)](https://www.cognizant.com/en_us/case-studies/documents/3879600_network-health-five-star-call-center-member-experience-fueled-by-qnxt_printable.pdf)
- [Cognizant: QNXT product page](https://www.cognizant.com/us/en/industries/healthcare-technology-solutions/trizetto/core-administration/qnxt)
- [Cognizant opens TriZetto Unify to AI agents (May 2026)](https://news.cognizant.com/2026-05-29-Faster-decisions,-faster-care-for-patients-Cognizant-opens-TriZetto-Unify-to-AI-agents)
- [Salesforce Health Cloud implementation costs (consultant estimate)](https://dev.to/christopher_gainus_84f002/salesforce-health-cloud-implementation-real-costs-and-timelines-2026-48kb)
- [Hitachi Solutions: health plan customer care transformation](https://global.hitachi-solutions.com/wp-content/uploads/2024/01/Health-Plan-Customer-Care-Transformation.pdf)
- [Pega Customer Service for Healthcare](https://www.pega.com/insights/resources/pega-customer-service-healthcare)
- [Zipari platform](https://zipari.com/platform)
- [Uniphore: after-call work automation case study](https://uniphore.com/case-studies/leading-us-health-insurance-company-increases-its-agents-productivity-by-automating-mandatory-tasks-with-uniphores-u-assist)
