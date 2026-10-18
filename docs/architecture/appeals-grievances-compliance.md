# Appeals, grievances and complaints: compliance by configuration

**Status:** Draft for review (2026-10-15)
**Scope:** How CHO tracks every member and provider complaint, grievance, coverage request and appeal against the rules for its line of business and state. It also covers distributing the work so nothing misses its clock, and proving all of it to an auditor from the system's own records.

**Related:**
- [cho-service.md](cho-service.md): where many of these cases start, on a call.
- [document-store.md](document-store.md): case files and letters.
- [correspondence.md](correspondence.md): generating and delivering the notices, and recording when they were sent.
- [appeals-case-model.md](appeals-case-model.md): member vs provider tracks, the requester party, and researched State rule packs.
- [claim-review-workbench.md](claim-review-workbench.md) §3.3: the same dated-rule approach, applied to prompt pay.

---

## 1. The wedge

Every plan carries the same burden: dozens of clocks across lines of business and states, each with its own levels, notices and exceptions, audited from the plan's own data.
- **Today:** plans manage this with spreadsheets, a case tool bolted onto the core system, and a scramble when the audit engagement letter arrives.
- **CHO's offer:** one engine where the rules are configuration, every deadline becomes assigned work, and the audit exports come straight from the record. Nothing gets reassembled for an audit.

What a plan gets:
1. **The right clock on every case, automatically.** The rules come from the line of business and state, dated, so a rule change has an effective date instead of a code change.
2. **Work goes to people before deadlines, not after.** Each case's next deadline is a work item, prioritized by time left and routed to someone qualified to decide it.
3. **A standing mock audit.** CMS's timeliness tests run every day against live data, so the plan sees a failing measure weeks before an auditor does.
4. **Export layouts that are versioned.** When CMS changes a layout, CHO ships a new template version; the plan's data doesn't change.

## 2. What the rules say

The rules differ in numbers, but they share a structure: a **case type**, a **chain of levels**, a **clock** for each level, **notices** with their own deadlines, and **what happens next**:
- escalation to an outside reviewer;
- deemed exhaustion when the plan misses its deadline;
- effectuation after a reversal;
- continued benefits while an appeal is pending.

### 2.1 Medicaid managed care (federal floor: 42 CFR 438 subpart F)

| Rule | Requirement |
|---|---|
| Levels | A plan **may have only one level of appeal for enrollees** (438.402(b)). After it, the member goes to a State fair hearing; some states add an optional external medical review. |
| Grievance resolution | Set by the State, **no more than 90 calendar days** from receipt (438.408(b)(1)). |
| Standard appeal | **No more than 30 calendar days** from receipt (438.408(b)(2)). |
| Expedited appeal | **No more than 72 hours** from receipt (438.408(b)(3)). |
| Extension | **Up to 14 calendar days**, if the enrollee asks or the plan shows it needs more information and that the delay is in the enrollee's interest. Not enrollee-requested: reasonable efforts at prompt oral notice, plus **written notice within 2 calendar days** (438.408(c)). |
| Deemed exhaustion | If the plan misses the notice or timing requirements, the enrollee **is deemed to have exhausted** the plan's appeal and may go straight to a State fair hearing (438.408(c)(3)). |
| State fair hearing | Requested **90–120 calendar days** (State's choice) from the plan's resolution notice (438.408(f)). |
| External medical review | Optional per State. It must be independent and free, must not delay the fair hearing, and must not extend any 438.408 timeframe (438.408(f)(1)(ii)). |
| Filing, continuation, effectuation | Appeals are filed within 60 days of the adverse benefit determination notice (438.402(c)). Benefits continue if the request is timely (438.420). After a reversal, the service is authorized within 72 hours (438.424). *Values to confirm against the eCFR text.* |
| Records | Each grievance and appeal is recorded with a general description, date received, date of each review, the resolution at each level, the date of resolution, and the member's name (438.416). |

### 2.2 Texas Medicaid and CHIP: a state layered on the floor

From the Texas Association of Health Plans' 2025 presentation, citing the Texas managed care contract and manual (UMCC/UMCM), SB 1207 (86R), and the Texas Administrative Code:
- **Member complaints:** resolved within **30 days**; 98% must meet it or the plan faces liquidated damages.
- **Member appeals:**
  - Standard: **30 calendar days**, with up to 14 days of extension.
  - Expedited: **72 hours**, or **one business day** for an ongoing emergency or a denial of continued hospitalization.
  - Benefits continue only if the appeal is filed by the effective date of the adverse determination.
- **State fair hearing:** requested within **120 days**. The final decision is due within 90 days, or 3 business days if expedited.
- **External medical review by an independent review organization:** chosen alongside a fair hearing, never alone.
  - The plan sends records to HHSC within **3 business days**, or **1 business day** if expedited.
  - The reviewer decides within **10 days** of having the records, or next business day if expedited.
  - If the reviewer reverses and benefits weren't continued, the plan reinstates them within **72 hours** (UMCM 3.21.1).
- **Provider claim appeals and recoupments:**
  - Filed within **120 days** of the disposition.
  - The plan adjudicates the appealed claim within **30 days**.
  - Providers may exhaust appeals before repaying.
- **Provider complaints:** after the plan's process, STAR providers may complain to HHSC, and CHIP providers to the Texas Department of Insurance.
- **Plan-defined levels:** plans set their own provider appeal levels in policy. One Texas MCO's policy uses a single level: 180 days to file, acknowledgment in 5 business days, resolution in 30 days. Others use a first and second level, each with its own turnaround.
- **Reporting:** complaint reporting to HHSC, published quarterly; average resolution times; an audit trail for interest payments.

**Why the rules must be dated:** the Texas Medicaid handbook section revised in 2014 states expedited member appeals as **3 business days**. Current rules say **72 hours**. A system with the timeframe hard-coded would have been wrong for years. A dated rule pack changes on an effective date and keeps the old rule for cases opened before it.

### 2.3 Medicare Advantage (42 CFR 422 subpart M)

| Case | Clock |
|---|---|
| Grievance | Filed within 60 days of the event, orally or in writing. Resolved in **30 days**, extendable by 14. **24 hours** for grievances about the plan taking an extension or refusing to expedite (422.564). |
| Standard pre-service determination | 14 days, or 28 with extension, in CMS's 2020 audit protocol. **CMS-0057-F shortened standard prior-authorization decisions to 7 calendar days from January 1, 2026.** Another example of why rules must be dated. |
| Expedited determination | 72 hours (+14 extension) |
| Payment determination | Clean claims from non-contracted providers paid within 30 days; other claims within 60 days (422.520) |
| Reconsideration | Filed within **65 days** of the notice. Pre-service **30 days**, Part B drug **7 days**, payment **60 days**, expedited **72 hours**. |
| Auto-forward | An upheld or late reconsideration is **forwarded automatically to the Independent Review Entity** (422.590). |
| Effectuation after reversal | IRE pre-service: **72 hours / 14 days**. IRE payment: paid within **30 days**. ALJ/Council: **60 days** (422.618, 422.619). |

## 3. The model

```
CaseType      Grievance | Complaint | CoverageRequest | Appeal | ExternalReview | FairHearing
              | ProviderComplaint | ProviderClaimAppeal | Effectuation
Case          id, tenant, type, level, lineOfBusiness, state, product, expedited, subjects[] (member, claim, auth, provider),
              requester (member | representative + AOR date | provider), receivedAt (with time zone), channel,
              rulePackVersion (pinned at open), status, events[], documents[] (document store), workItemId
CaseEvent     append-only: Received, Acknowledged, ExtensionTaken(reason, requestedBy), InfoRequested, InfoReceived,
              ClinicalReviewCompleted(reviewerId, credentials), Decided(disposition), OralNoticeGiven, WrittenNoticeSent,
              ForwardedToIre, ReversalReceived, Effectuated, Dismissed(reason), Withdrawn, Reopened, Escalated(nextLevel)
              — each with occurredAt (the real-world time, not the keying time), recordedAt, recordedBy, source
```

**Rule pack:** a versioned, effective-dated configuration per *line of business × state (× product)*:

```
levels:      [ { caseType, level, name ("Level 1 provider appeal"), filingWindow, nextLevel, nextLevelFilingWindow } ]
clocks:      { caseType, level, expedited } → { duration, unit: hours|calendarDays|businessDays, startsAt: Received|InfoReceived,
               extension: { maxDays, allowedWhen, oralNotice, writtenNoticeWithin } , pausesOn: [] }
notices:     { acknowledgment: within N business days, resolution: written (+ oral if expedited), extension: 2 calendar days }
onMissed:    DeemedExhaustion | AutoForwardToIre | EscalateToSupervisor
onUpheld:    AutoForwardToIre (MA) | OfferFairHearing+EMR (TX Medicaid)
onReversed:  Effectuate within { duration }
continuation:{ requestWithin, untilEvent }
reviewers:   { medicalNecessity: physician, reconsideration: notInvolvedInOriginal }
```

**How rule packs work:**
- **Shipped by CHO:** packs for federal Medicaid (the 438 floor), Medicare Advantage, and each supported state (Texas first). A tenant can tighten any value but can't loosen a regulatory one below the floor; CHO validates that at save time.
- **Plan-chosen levels are configuration.** A plan with Level 1 and Level 2 provider appeals, then escalation to the state, configures exactly that, each level with its own turnaround.
- **Pinned at open.** A case keeps the pack version it opened under; a rule change applies to new cases from its effective date.

## 4. Distributing the work

The point of compliance isn't the report; it's that people decide cases on time. Every clock becomes work:

- **Each open case has one work item** whose due time is the case's **next deadline**: acknowledgment, extension notice, decision, written notice, IRE forward, effectuation. As each step completes, the due time moves to the next deadline.
- **Priority is time remaining against total time.** A 72-hour expedited appeal with 6 hours left outranks a 30-day appeal with 5 days left. The priority explanation already shown in My Work ("due in 12d") carries over.
- **Routing respects who may decide:**
  - Medical-necessity decisions route to physician queues.
  - **Get Next never hands a reconsideration to the person who made the original decision.** The engine enforces the separation; it isn't left to remembering.
  - Expedited cases have their own queues with on-call coverage.
- **Downstream steps are automatic.** An upheld MA reconsideration creates the IRE forwarding item. A reversal creates an effectuation item with its own clock, routed to whoever can authorize or pay. A missed Medicaid deadline records deemed exhaustion and sends the member the fair hearing notice.
- **Demand and capacity view:** incoming volume by case type, the deadlines falling due each day for the next two weeks, and the hours of qualified staff in each queue. A supervisor sees Thursday's shortfall on Monday.

## 5. Proving it

- **Everything is an event.** Dates in exports are derived from case events, never typed into a report. Each event records when it happened (with time zone) and when and by whom it was recorded. CMS rejects universes with inconsistent time zones, and requires good-cause and receipt dates to be when information *arrived*, not when a letter printed.
- **Universe exports are versioned templates.** CMS's ODAG universes have changed shape: the 2020 protocol had 13 tables (SOD, EOD, Claims, DMR, SREC, EREC, PREC, IREEFF, IREClaimsEFF, ALJMACEFF, GRV_S, GRV_E, DIS); the 2021 training shows 6 consolidated tables (OD, RECON, PYMT_C, EFF_C, GRV_C, AIP). CHO keeps each layout as a template with its version, so a plan exports exactly what its engagement letter asks for. *The current layout will be checked against the protocol in effect when the export is built.*
- **Self-check before export:** CHO runs CMS's data-integrity checks first:
  - received ≤ decision ≤ notice;
  - required fields populated, "None" where the layout directs;
  - times all in one zone;
  - exclusions applied (withdrawn dismissals, reopenings, retrospective reviews).
- **Standing mock audit:** CMS's timeliness tests (decision, notification, auto-forward, effectuation, grievance notification) run daily against the live universe. The dashboard shows each measure against the thresholds CMS uses for conditions and corrective action, with the failing cases one click away.
- **Impact analysis:** when a problem is found, the affected-case list exports in the same universe layout, which is what CMS asks for.
- **State reporting:** Texas quarterly complaint and appeal reports, average resolution times, and EMR record transmittals to HHSC, each as a template on the same events.

## 6. Build order

1. **Case model and event log**; rule pack schema with validation against the federal floors; Texas Medicaid, federal Medicaid and MA packs.
2. **Clocks to work items:** next-deadline work items, priority by time remaining, reviewer-separation routing, expedited queues.
3. **Notices:** acknowledgment, extension and resolution letters generated into the document store, with sent events taken from delivery proof rather than typed in (see [correspondence.md](correspondence.md)).
4. **Downstream automation:** IRE auto-forward, deemed exhaustion, effectuation clocks, continuation of benefits.
5. **Exports:** ODAG universe templates (current protocol), Texas quarterly reports; the integrity checks.
6. **Mock audit dashboard and demand/capacity view.**

## 7. Open questions

1. **First pack beyond Texas and MA:** which states matter most for the pipeline? Arizona (AHCCCS) is a natural second.
2. **Commercial and exchange plans** (TDI, ERISA) in the first release, or after Medicaid and MA?
3. **Current CMS protocol:** an updated program audit data collection was filed with OMB in 2026. The template set should be built against it once it's final.

## Sources

- [42 CFR 438.408: Medicaid resolution and notification](https://www.law.cornell.edu/cfr/text/42/438.408)
- [42 CFR 422.564: MA grievances](https://www.law.cornell.edu/cfr/text/42/422.564)
- [CMS: Reconsideration by the MA plan](https://www.cms.gov/Medicare/Appeals-and-Grievances/MMCAG/PartCRecon)
- [CMS Part C ODAG audit process and data request (2020)](https://www.hhs.gov/guidance/sites/default/files/hhs-guidance-documents/2012294628-nx-2020cdauditprotocol_attachment_iv_odagauditprocessdatarequest.pdf)
- [CMS 2021 ODAG audit training](https://CMS.gov/files/document/2021-audit-training-odagd12508.pdf)
- [TAHP: Complaints, prior authorization, appeals and fair hearings (2025)](https://tahp.org/wp-content/uploads/2026/01/2025.04.26-Complaints-PA-Appeals-and-Fair-Hearings-Presentation.pdf)
- [Texas UMCM 3.21.1: Independent Review Organization process](https://fhb.hhs.texas.gov/sites/default/files/documents/3-21-1.pdf)
- [Texas HHS handbook 4000: complaints, internal MCO appeals and fair hearings](https://hhs.texas.gov/book/export/html/4626)
- [Texas MCO provider complaints and appeals policy (example)](https://chppayment.christushealth.org/documents/2024/Policy/Provider%20Complaints%20and%20Appeals%20OPCGA17%20G.pdf)
- [1 TAC § 353.415: Texas member complaint and appeal procedures](https://www.law.cornell.edu/regulations/texas/1-Tex-Admin-Code-SS-353-415)
