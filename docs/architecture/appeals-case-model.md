# Appeals case model: members, providers and State rule packs

**Status:** Draft for review (2026-10-17). Regulatory research as of 2026-10-17.
**Scope:** How appeals-service moves from a claim appeal record to the case model in [appeals-grievances-compliance.md](appeals-grievances-compliance.md) §3, so that it can tell **who** is disputing **what** and apply the right rules:
- member appeals and grievances, including pre-service appeals with no claim;
- provider claim disputes and provider complaints, which follow contract and State rules rather than member rules;
- providers acting for members, which follow member rules;
- per-State rule packs, starting with Texas, California, Florida, New York, Colorado, Arizona, Illinois, Oklahoma, Michigan and Ohio;
- holding recoupment while a provider dispute is open, where a State requires it.

**Related:**
- [appeals-grievances-compliance.md](appeals-grievances-compliance.md): the case model, rule pack schema, work distribution and audit exports this document builds on.
- [correspondence.md](correspondence.md): the notices each rule pack requires.
- [cho-service.md](cho-service.md): cases opened from calls.

> **Not legal advice.** §5 summarizes federal and State rules as researched on 2026-10-17, with citations. Each pack is checked against current text and the plan's contract before it ships. Items marked *to confirm* were not verified against primary text.

---

## 1. Why change it

**What appeals-service is today:**
- **A provider claim appeal record.** `ClaimId`, `ClaimNumber` and `ProviderNPI` are required in the model, the create API, the shared `AppealDto` contract and the FHIR mapping, whose requester is always `Practitioner/{NPI}`. The default intake source is the provider portal and the reason field is "the provider's argument."
- **With member clocks.** `AppealResponseDeadlinePolicy` applies the member regulatory clocks (42 CFR 422.590, 438.408, ERISA, ACA) by line of business, appeal type and level.
- **Solid foundations:** intake from the portal, Availity with X12 275 attachments, and FHIR; first, second and external levels; extensions with the written-notice timestamp; a holiday calendar; an outbox; the access log; Appeals & Grievances Insights.

**What goes wrong:**
1. **Provider and member cases get the same clock.** Most provider payment disputes are outside the member rules. For Medicare Advantage, a contracted provider's payment dispute is governed by its contract, not Part 422 subpart M (§422.562(c)(2)). Medicaid sets no federal provider dispute process at all; States do. Applying 422.590 or 438.408 to these cases gives the wrong due date, and puts them in member audit universes where they don't belong.
2. **No record of who filed.** `SubmittedBy` is the logged-in user. There's no requester party, no representative and no consent link (two TODOs mark this).
3. **Cases without a claim can't exist.** A pre-service member appeal of a denied authorization, a member grievance about access or quality, and a provider complaint about credentialing or policy all have no claim, and `ClaimId` is required.
4. **No provider complaint type.**
5. **Rules are hardcoded federal values.** A plan's Level 1 and Level 2 provider appeals, or a State's stricter clock, can't be configured. reference-data-service already holds `AppealStandardDays` and `AppealExpeditedHours` per State in `StateComplianceConfig`, which appeals-service never reads.
6. **Recoupment never waits.** Recoupment is a reversal run and a ledger entry; nothing checks for an open dispute, though several States forbid offsetting a contested overpayment (§6).

## 2. Who files and which track applies

The track decides the clock, the notices, the audit universe and who gets the letters. It's set at intake from **who is filing, on whose behalf, and the line of business**, and recorded on the case.

| Who files | For whom | Track | Rules |
|---|---|---|---|
| Member | Self | **Member** | Member clocks and notices (422 subpart M, 438 subpart F, State law) |
| Representative (AOR, POA, guardian) | Member | **Member** | Same; notices also to the representative |
| Treating physician, MA **pre-service** | Member | **Member** | Allowed without an AOR after notifying the enrollee (422.578, 422.582(a)); a physician acting on the enrollee's behalf may request expedited |
| Provider with member's written consent, Medicaid | Member | **Member** | Treated as the enrollee except it can't request continuation of benefits (438.402(c)(1)(ii)); only where State law permits |
| **Non-contracted** provider, MA, payment denial | Self | **Member subpart M, provider variant** | Needs a signed **waiver of liability**; 60 days to file (65 from the remittance date); plan decides within 60 days or forwards to the IRE; notices go to the provider (422.574(b), 422.582, 422.590(b); CMS PCD Guidance §50.1.1); the IDN may not be sent to non-contract providers (PCD Guidance, notice section) |
| Non-contracted provider, MA, **amount** paid only | Self | **Payment dispute** | Plan payment review, then the CMS Payment Dispute Resolution Contractor; not subpart M (422.214). *Current contractor and process document to confirm* |
| **Contracted** provider, MA, payment | Self | **Provider contract dispute** | Provider contract terms; no subpart M rights when the enrollee owes nothing further (422.562(c)(2)) |
| Provider, Medicaid, own behalf | Self | **Provider dispute (State)** | State law and the State contract (§5) |
| Provider, commercial | Self | **Provider dispute (State)** | State law and contract (§5) |
| Member or provider, about service or behavior, not a determination | — | **Grievance / complaint** | Member grievance or provider complaint rules |

**Rules for the track:**
- A request missing what its track needs (a waiver of liability, an AOR, member consent) doesn't silently switch tracks. It stays on its track with a work item to obtain the document, and is dismissed only as the rules allow (for MA, a missing waiver of liability at the end of the adjudication time, PCD Guidance §50.1.1; missing representative documentation, §20.2.1, *to confirm*).
- A provider may file on one claim both for itself and, with an AOR, for the member. Those are two cases, linked.
- The track is pinned when the case opens, like the rule pack version, and changes only by a recorded correction.

## 3. The case model

Building on [appeals-grievances-compliance.md](appeals-grievances-compliance.md) §3:

```
Case        id, tenantId, caseNumber, track (Member | ProviderSubpartM | PaymentDispute | ProviderContract | ProviderState),
            caseType (MemberAppeal | MemberGrievance | ProviderClaimDispute | ProviderComplaint | ExternalReview | FairHearing | Effectuation),
            level (rule-pack level id), lineOfBusiness, state, product, expedited, rulePackVersion (pinned at open),
            requester { party: Member | Representative | Provider, memberId?, npi?, name, onBehalfOf: Self | Member,
                        authority?: { kind: AOR | POA | Guardian | WrittenConsent | TreatingPhysicianPreService | WaiverOfLiability,
                                      documentId?, receivedAt } },
            subjects[] { kind: Claim | ClaimLine | Authorization | Member | Provider | PaymentRun | Recoupment, id },
            receivedAt (with time zone), channel, status, events[], documents[], workItemId, linkedCases[]
```

**What changes from today's `Appeal`:**
- `ClaimId`, `ClaimNumber` and `ProviderNPI` move into `subjects[]` and `requester`. A claim is required only for case types whose rule pack says so (provider claim disputes).
- `AppealType` (Reconsideration, PeerReview, ExternalReview, Grievance) splits into `caseType` and `level`. Peer review becomes a step inside a case, not a type.
- `AppealLevel` (First, Second, External) becomes a rule-pack level id, so a State with one plan level and one with two both fit.
- `AppealSource` gains `Fax`, `Mail`, `Phone` and `FhirApi`, and is actually set by each intake path (today `Availity275` and three other values are never assigned).
- `SubmittedBy` stays as the user who keyed it; `requester` is the party.

## 4. Rule packs

A rule pack is versioned, effective-dated configuration per **line of business × jurisdiction (× product)**, where the jurisdiction is a State or, where the clocks are federal, the nation, with these additions to the schema in [appeals-grievances-compliance.md](appeals-grievances-compliance.md) §3:

```
tracks:      which tracks exist and what each requires (claim subject, authority document)
levels:      per caseType and track: [ { id, name, owner: Plan | State | IndependentReviewer | Agency,
               filingWindow { duration, from: NoticeDate | NoticeReceipt | DecisionDate | DateOfService | Payment },
               acknowledgment { duration, unit }, resolution { standard, expedited }, extension { maxDays, notice },
               onMissed: DeemedExhaustion | Reversal | DeemedDenied | AutoForward, next[] } ]
units:       calendarDays | businessDays | workingDays | hours, with the State's holiday calendar
continuation:{ requestWithin, untilEvent, providerMayRequest: bool }
recoupment:  { holdWhileDisputed: bool, contestWindow, offsetRequires: [ContractClause, Uncontested], lookback }
notices:     see correspondence.md §5
```

**Rules for packs:**
- **Level chains are per State.** Texas's provider path (plan levels, then the State) is one example, not the default. Some States have one plan level then a State hearing; some add an independent reviewer or a State dispute program; New York has two levels for complaints; some leave provider disputes entirely to each plan's contract.
- **Units are explicit.** Calendar days, business days and working days all appear in State rules; the pack names the unit for every clock.
- **Stricter wins, and conflicts are recorded.** Where a State rule and contract differ (for example Arizona's rule text and contract on the fair hearing window), the pack carries the value the plan must meet, the other value, and the source of each.
- **Plan-defined values** (a plan's own provider dispute window where the State sets none) are tenant settings inside the pack, validated against any State floor.
- **Seeded from what exists.** `StateComplianceConfig` in reference-data-service becomes the home for State packs; its current `AppealStandardDays` and `AppealExpeditedHours` values move into the member appeal level of each pack.

### 4.1 Federal floors, per line of business

Every pack is measured against a floor it may tighten and never loosen: `RulePackFloor`, whose ceilings all come from `AppealResponseDeadlinePolicy` rather than being restated, so the policy appeals-service enforces on real appeals and the floor packs are validated against cannot drift apart. A pack whose line of business has no floor is refused at startup rather than guessed at.

| | Medicaid (42 CFR 438 subpart F) | Medicare Advantage (42 CFR 422 subpart M) |
|---|---|---|
| Appeal resolution | 30 days / 72 hours expedited | 30 days / 72 hours expedited |
| Grievance resolution | 90 days | 30 days |
| Filing window | exactly 60 days | at least 60 days (a plan may give longer) |
| Extension | 14 days | 14 days, standard **and** expedited |
| State fair hearing | 90–120 days, required | **none** — an upheld decision goes to the IRE |

*Before 2026-10-23 there was one floor, written as Medicaid constants, and the validator refused every other line of business. Part D or Marketplace is now a row in `RulePackFloor`, not a rewrite.*

## 5. Packs

Medicaid managed care first, which is CHO's main market, and Medicare Advantage as the one federal pack; commercial values are noted where researched. Federal floors apply everywhere unless a pack is stricter. Each State's level chain is shown as a line, then the values.

### 5.1 Texas (Medicaid/CHIP)

- **Member:** plan appeal → State fair hearing (120 days to request); External Medical Review through an independent review organization.
- **Provider:** plan complaint or claim appeal (plan-defined levels) → HHSC (STAR) or TDI (CHIP).

| Item | Value | Source |
|---|---|---|
| Member appeal: file / acknowledge / resolve | 60 days / 5 business days / 30 days (72 hours expedited), +14 | UMCM 3.21 (v2.6, 2022; *current version to confirm*); 42 CFR 438.408 |
| Advance notice of reduction or termination | 15 business days (stricter than federal 10 days) | UMCM 3.21 |
| Provider claims-payment appeals | MCO must track and resolve them; unresolved medical-necessity denials go to a same-specialty out-of-network physician whose decision binds both | Gov't Code §540.0267 |
| Provider complaints (CHIP HMO) | Acknowledge by 5th business day; resolve by 30th calendar day | Ins. Code §843.252 |
| Provider complaint and claim appeal windows | 180 days to file a complaint; 120 days to appeal a claim; 30 days to resolve | UMCC §8.2.4.1 / §8.4.1 (*to confirm; HHSC documents unreachable*) |
| Recoupment hold | Fraud and abuse recoupment only if the provider doesn't appeal or loses (1 TAC §353.1454(b)); EVV-based overpayments not recovered until appeals are exhausted (Gov't Code §544.0503(b)); general hold *to confirm* | |

### 5.2 California (Medi-Cal and Knox-Keene)

- **Member:** plan appeal (one level) → State Hearing (120 days); Independent Medical Review (180 days) for Knox-Keene plans, before or alongside the hearing, never after.
- **Provider:** plan dispute → DMHC Provider Complaint Unit (Knox-Keene, after 45 working days) → DHCS.

| Item | Value | Source |
|---|---|---|
| Member appeal: file / acknowledge / resolve | 60 days / 5 calendar days / 30 days (72 hours expedited), +14 | DHCS APL 21-011 (Rev. 8/2022) |
| Member continuation | Appeal within 10 days of the notice; State Hearing within 10 days of the resolution | APL 21-011 |
| Untranslated notice | Deemed exhaustion | APL 21-011 |
| Member grievance | File within 180 days; acknowledge 5 calendar days; resolve 30 days, all internal levels included; urgent 72 hours (Medi-Cal) or 3 days with DMHC notice (Knox-Keene) | 28 CCR 1300.68; H&S Code §1368, §1368.01 |
| Provider dispute | Filing window of at least 365 days; acknowledge 2 working days (electronic) or 15 (paper); written determination within 45 working days; pay within 5 working days if overturned | 28 CCR 1300.71.38; DHCS APL 26-017 |
| Recoupment | Request within 365 days of payment; provider contests within 30 working days as a provider dispute; **no offset while contested**; offset only when the notice is uncontested, the provider hasn't repaid within 30 working days, and the contract allows it | 28 CCR 1300.71(b)(5), (d) |

### 5.3 Florida (SMMC)

- **Member:** plan appeal → Medicaid Fair Hearing (120 days); a plan may elect an independent review organization.
- **Provider:** plan complaint → statewide Claim Dispute Resolution program (§408.7057, run by an independent organization) → AHCA final order.

| Item | Value | Source |
|---|---|---|
| Member appeal: file / acknowledge / resolve | 60 days / 5 business days / 30 days, **expedited 48 hours** (stricter than federal 72) | SMMC Attachment II §VI.F–H (July 2026) |
| Plan-taken extension | Oral notice same day; written within 2 calendar days; resolution notice within 44 days | Att. II §VI.G |
| Member grievance | Acknowledge 5 business days; resolve 30 days, +14 | Att. II §VI.D |
| Provider claim complaint | File within 90 days; acknowledge 3 business days; status notice every 30 days; resolve 60 days (denied claims) or 90 days | Att. II §VII.D.6.e |
| Provider non-claim complaint | File within 45 days; acknowledge 3 business days; resolve 90 days | Att. II §VII.D.6.d |
| State dispute program | File within 12 months of the plan's final determination; recommendation within 60–90 days; AHCA order within 30 days; loser pays | F.S. §408.7057 |
| Recoupment | 30-month lookback (12 months for providers licensed under chapters 458–461, 466 and 490); provider contests within 35 days; **no offset without written consent or a missed response** | F.S. §641.3155(5), (16) |

### 5.4 New York (Medicaid managed care)

- **Member appeal:** plan appeal → fair hearing (120 days) and/or State external appeal; the fair hearing decision controls.
- **Member complaint:** complaint → complaint appeal (a second plan level) → DOH.
- **Provider:** retrospective UR appeal → State external appeal (concurrent and retrospective only).

| Item | Value | Source |
|---|---|---|
| Member appeal: file / acknowledge / resolve | 60 days / 15 days / 30 days; expedited 72 hours (written notice within 24 hours of oral) | DOH timeframe comparison (rev. 6/2026) |
| Missed UR appeal deadline | **Denial reversed** | PHL §4904 |
| Complaint | Acknowledge 15 business days; resolve 30 days (referral and coverage) or 45 days (other), expedited 48 hours | PHL §4408-a(4) |
| Complaint appeal | Filing window of at least 60 business days; resolve 30 business days, urgent 2 business days | PHL §4408-a(8)–(11) |
| Provider UR appeal | Retrospective only on its own; filing window of at least 45 days; acknowledge 15 days; decide within 30 days of receiving the needed information | PHL §4904 |
| Provider external appeal | 60 days to file; reviewer fee may shift to the provider when a concurrent denial is upheld | PHL §4910, §4914 |
| Recoupment | 30 days' notice; 24-month lookback; challenge procedure required; **no stated hold** | Ins. Law §3224-b |

*Model Contract Appendix F and any model-contract provider claim dispute process to confirm.*

### 5.5 Colorado (Health First Colorado)

- **Member:** plan appeal → State Fair Hearing at the Office of Administrative Courts.
- **Provider (managed care):** each plan's process; no State-set process found.

| Item | Value | Source |
|---|---|---|
| Member appeal: file / acknowledge / resolve | 60 days / **2 working days** / **10 working days** (72 hours expedited), +14 | 10 CCR 2505-10 §8.209.4 |
| Fair hearing window | 120 days per the rule; the member-facing page says 60. **Configure 120 and confirm with HCPF** | §8.209.4.N |
| Member grievance | Acknowledge 2 business days; resolve 90 days; safety issues 72 hours | §8.209.5 |
| Provider (fee-for-service) | Informal reconsideration within 30 days, then appeal to the OAC within 30 days | §8.050 |
| Recoupment (fee-for-service) | **Not implemented until the appeal is complete** | §8.050.3.D |
| Recoupment (managed care) | No State rule found; plan contract governs (*to confirm*) | |

### 5.6 Arizona (AHCCCS)

- **Member:** contractor appeal → State Fair Hearing (request filed with the contractor, sent to AHCCCS, heard by an ALJ) → Director's Decision. No external medical review.
- **Provider:** contractor claim dispute → State Fair Hearing → Director's Decision.

| Item | Value | Source |
|---|---|---|
| Member appeal: file / acknowledge / resolve | 60 days / 5 business days (1 day expedited) / 30 days (72 hours expedited), +14; missed deadline is **deemed denied** | R9-34-209, -213; ACC-RBHA contract Att. F1 |
| Fair hearing window | Contract 90 days; rule text still 30, below the federal 90-day minimum in 42 CFR 438.408(f)(2). **Configure 90, record both** | Att. F1 ¶48; R9-34-217 |
| Member grievance | Resolve within 10 business days, never more than 90; no fair hearing | Att. F1 ¶16; R9-34-212 |
| Provider claim dispute | File by the latest of 12 months from service, 12 months from eligibility posting, or 60 days after denial of a timely claim (statute); the contract extends the 60 days to payment, denial or recoupment; acknowledge 5 business days; decision within 30 days; hearing request within 30 days | A.R.S. §36-2903.01(B)(4); Att. F2; R9-34-405 |
| Recoupment | AHCCCS approval for recoupments over 12 months after payment or above the dollar threshold; notice must state the right to file a claim dispute; hold while disputed *to confirm* | ACOM 412 (10/2026) |
| Notices | AHCCCS NABD template; 6th-grade reading level, 12-point minimum | ACOM 414, 404 |

*Contract text read is the 2023 amendment; confirm against the current contract.*

### 5.7 Illinois (HealthChoice Illinois)

- **Member:** plan appeal → State Fair Hearing (120 days) and/or external independent review (30 days to request).
- **Provider:** MCO internal process → HFS Provider Complaint Portal → HFS decision (final).

| Item | Value | Source |
|---|---|---|
| Member appeal: file / resolve | 60 days / **15 business days**, **expedited 24 hours** after information received, +14 | HCI contract model (Aug 2025) §5.30.3 |
| Member grievance | Acknowledge **48 hours**; resolve 90 days | §5.30.1 |
| Provider dispute (MCO) | Substantive response within 30 days | §5.29.7.4 |
| Provider Complaint Portal | File no sooner than 30 days after the internal filing, and no later than 30 days after an unsatisfactory resolution or 60 days after the internal filing (HFS notices treat 60 days after filing as the outer limit); MCO proposal 30 days; HFS review decision final | 305 ILCS 5/5-30.1(g-8); 89 IAC 140.75 |
| Recoupment | Remittance must explain it; 60 days to appeal; 12-month lookback, except fraud and recoupment HFS requires under federal Medicaid rules (215 ILCS 5/368d); fraud recoveries need OIG approval and a provider appeal first (contract §5.35.10) | |

*New HealthChoice contracts were awarded in June 2026; confirm against the new contract text when published.*

### 5.8 Oklahoma (SoonerSelect)

- **Member:** plan appeal → State Fair Hearing through OHCA (120 days). No external medical review.
- **Provider:** reconsideration → plan appeal panel → OHCA administrative appeal. No State Fair Hearing for providers under the rule (the statute's wording conflicts; see §9).

| Item | Value | Source |
|---|---|---|
| Member appeal: file / acknowledge / resolve | 60 days / 5 calendar days / 30 calendar days, +14; resolution notice within 3 calendar days | OAC 317:2-3-5 |
| Expedited | Required when the standard timeline could jeopardize health; hours not stated (federal 72 hours applies; *to confirm in contract*); step therapy 24 hours if exigent, otherwise 72 hours | OAC 317:2-3-5(g)(1) |
| Member grievance | Acknowledge 10 calendar days; resolve 90 days, +14 | OAC 317:2-3-4 |
| Provider reconsideration | Within 6 months of a claim denial (statute and rule); 15 days for audit findings and terminations (rule); resolution notice within 5 calendar days of resolving; no acknowledgment deadline | OAC 317:2-3-10(c); 56 O.S. §4002.8(C) |
| Provider appeal | Panel of 3 or 5 uninvolved reviewers; medical necessity reviewed by a same- or similar-specialty clinician, no automated review software | OAC 317:2-3-10(d); 56 O.S. §4002.8(D)–(E) (HB 1810, eff. 11/1/2025) |
| OHCA administrative appeal | Within 30 days of a plan appeal decision not fully in the provider's favor; plan sends the file within 15 days | OAC 317:2-3-10(d)(4)–(5), 317:2-3-13 |
| Prompt pay | 90% of clean claims within 14 days; 1.5% monthly interest on late clean claims | 56 O.S. §4002.7 |
| Recoupment (from 1/1/2027) | 24-month audit lookback from claim submission; at least 60 days after an overpayment notice to file a corrected claim, with recoupment limited to the excess; no extrapolation; audit appeal to OHCA and an ALJ; **disputed funds recouped only after all appeals are final** | 63 O.S. §5051.11 (SB 1645, signed May 1, 2026) |

### 5.9 Michigan (Medicaid Health Plans)

- **Member:** plan appeal → State Fair Hearing through MOAHR (120 days); optional DIFS external review under the Patient's Right to Independent Review Act.
- **Provider:** non-contracted hospitals under the Hospital Access Agreement: plan appeal → reconciliation group → binding State Rapid Dispute Resolution. Other providers: plan appeal → binding arbitration.

| Item | Value | Source |
|---|---|---|
| Member appeal: file / resolve | 60 days (expedited request within 10 days) / 30 days, +14; expedited 72 hours. Acknowledgment required, no deadline set | CHCP contract (FY24, updated 6-2024) §1.1.XIII.H |
| Notices | Below a 6.9 reading level; prevalent languages; alternative formats | CHCP §XIII.H.6.c |
| DIFS external review | 120 days to request; IRO recommends in 14 days; director decides in 7 business days. Expedited: 10 days to request with a physician's support; IRO in 36 hours, director 24 hours after | MCL 550.1911, 550.1913 |
| Member grievance | Resolve 90 days (+14 under federal rules); no acknowledgment deadline set | CHCP §XIII.H.1.c.v; 42 CFR 438.408(c) |
| Hospital disputes (HAA) | Reconciliation group at least every 90 days; then Rapid Dispute Resolution: mediator in 15 days, session in 15, decision in 15, payment in 30 (either direction); binding | Hospital Access Agreement §1.10.1; CHCP §XIV.F.6.b; MDHHS RDRP |
| Other providers | Binding arbitration after the plan's internal appeal, from an MDHHS arbitrator list | CHCP §XIV.F.6.c–d |
| Prompt pay | Clean claims in 45 days with 12% interest; defect notice in 30 days | MCL 400.111i |
| Recoupment | No statewide notice, lookback or hold rule found; limits on recoupment after retroactive disenrollment | CHCP §XIV.F.4 (*hold to confirm*) |

### 5.10 Ohio (Next Generation managed care)

- **Member:** plan appeal → State hearing (90 days). No external review through the Department of Insurance for Medicaid.
- **Provider:** internal claim dispute → ODM External Medical Review for medical-necessity denials (binding on the plan if it reverses) or the ODM complaint portal (not binding) for other disputes.
- **Integrated duals:** Next Generation MyCare started 1/1/2026 in the original MyCare counties and phased in statewide from April to August 2026, with integrated appeals under 42 CFR 422.633 as applicable (OAC 5160-58-08.4) and External Medical Review available to its members (ODM Next Gen MyCare provider FAQ).

| Item | Value | Source |
|---|---|---|
| Member appeal: file / acknowledge / resolve | 60 days / 3 business days (written) / **15 calendar days**; expedited 72 hours | OAC 5160-26-08.4(D)–(E) |
| Extension | Up to 14 days, **only with ODM approval** before the deadline | OAC 5160-26-08.4(F) |
| Advance notice of reduction or termination | 15 calendar days | OAC 5160-26-08.4(B)(3) |
| Continuation | Appeal within 15 days of the notice; hearing within 15 days of the decision | OAC 5160-26-08.4(G)–(H) |
| Required forms | ODM 04043, 04066, 04046 (appeal outcomes); ODM 01959 (appeal summary for the hearing) | OAC 5160-26-08.4(G) |
| Member grievance | Acknowledge 3 business days; resolve 2 business days (access), 60 days (claims) or 30 days (other) | OAC 5160-26-08.4(C) |
| Provider claim dispute | File within the later of 12 months from service or 60 days after payment or denial; acknowledge 5 business days; resolve 30 business days (medical necessity) or 15 business days (other) | ODM MCO Provider Agreement (rev. 7/2024) App. A |
| External Medical Review | 30 days to request after the plan's decision; 30 days standard, 3 business days expedited; plan pays; binding on the plan if reversed | ODM EMR FAQ; Provider Agreement |
| Recoupment | 2-year lookback; provider has 30 days to dispute; plan decides in 30 days; **recovery waits for the written determination**, then may be executed within 3 business days of sending it; notice content per ORC 5167.22 | Provider Agreement App. G §11 |

### 5.11 Medicare Advantage (Part C) — national

**Member:** plan reconsideration → Independent Review Entity (a CMS contractor) → OMHA Administrative Law Judge → Medicare Appeals Council → federal district court.

- Filing: 60 calendar days from the organization determination notice (422.582(b)); the plan may accept a late filing for good cause.
- Resolution: 30 calendar days pre-service (422.590(a)), 72 hours expedited (422.590(d)(1)), each extendable by 14 days.
- Grievance: 30 calendar days (422.564(e)(1)), extendable by 14; 24 hours for a grievance about a refusal to expedite or an extension the plan took (422.564(f)).
- **No State fair hearing.** When the plan upholds its denial in whole or part, or misses its own deadline, *the plan itself* forwards the file to the IRE (422.590(c), 422.592) — the member files nothing. That is the `autoForwardToExternalReview` outcome, which Medicaid has no equivalent of.
- **Provider:** CMS sets no process for a contracted provider, so the contract governs (`planDefined`). A non-contracted provider becomes a party by signing a waiver of liability (422.574(b)) and then uses the enrollee's path, IRE included.

Three clocks the pack deliberately does not carry, all because the appeal model has no pre-service / payment / Part B drug discriminator and `AppealResponseDeadlinePolicy` has enforced the conservative clock since it was written: payment reconsiderations (60 days, 422.590(b)) and Part B drug reconsiderations (72 hours standard, 24 hours expedited, no extension, 422.590(c)). Carrying the longer one would make the pack disagree with the ceiling the service actually applies. They are in the pack's `notes` and `toConfirm`.

### 5.12 What the States show

- **Member clocks vary more than expected:** expedited appeals of 24 hours (Illinois), 48 hours (Florida) and 72 hours elsewhere; standard resolution of 10 working days (Colorado), 15 business days (Illinois) and 15 calendar days (Ohio) against the federal 30 days; Ohio requires State approval for any plan extension.
- **Missed deadlines mean different things:** deemed exhaustion (most, including Arizona's "deemed denied"), reversal of the denial (New York utilization review), and in Medicare Advantage the plan sending the case on to the IRE itself.
- **Provider escalation differs everywhere:** a State agency (Texas, Illinois, Oklahoma), a State hearing (Arizona), an independent program (Florida), a regulator's complaint unit (California), a State external appeal (New York), State-run external medical review (Ohio), binding mediation or arbitration (Michigan), or only the plan's own process (Colorado managed care).
- **Recoupment holds are State-specific:** explicit in California, Florida, Ohio, Colorado fee-for-service and Oklahoma (from 2027); partial in Texas and Illinois; not stated in New York or Michigan.
- **Integrated duals are arriving:** Ohio's Next Generation MyCare (phased in during 2026) and Michigan's highly integrated D-SNPs (2026) need integrated notices and appeals, which [correspondence.md](correspondence.md) and these packs both have to support.

## 6. Recoupment while a dispute is open

- **Recoupment checks for an open dispute.** Before a recoupment or offset runs (a reversal run in claims-service, the ledger entry in ar-service), it asks appeals-service whether the claim has an open provider dispute and applies the pack's `recoupment` rule: hold, proceed, or proceed only with a contract clause.
- **Contesting an overpayment opens a case.** Where a State treats a contest as a provider dispute (California, Florida), the contest creates a `ProviderClaimDispute` with the recoupment as its subject.
- **Holds are visible.** A held recoupment shows on the claim, the provider record and the payment run, with the case that holds it, and releases when the case closes in the plan's favor.

## 7. Migration

- **Existing appeals are provider claim disputes,** since that's what the model captured: `track` from line of business and contract status where known (otherwise a review work item), `caseType` from `AppealType` (Grievance → member grievance with the member as requester only where the record shows it; otherwise flagged for review), `requester` = Provider with the NPI, `subjects` = the claim.
- **Schema version.** Cases get a `schemaVersion`. The migration follows `AppealStatusMigrationHostedService` (batched, idempotent, audited) for Mongo, with an admin script for Cosmos, matching the existing pattern and the decision to standardize on MongoDB.
- **Contracts and events.** `AppealDto` and the `AppealCreated` event move to version 2.0 with optional claim and NPI fields, published alongside 1.0 until consumers move. The drift test covers both.
- **Intake keeps working.** The X12 275 consumer still matches by claim for claim-based cases; the FHIR mapping sets `Task.requester` from the requester party (`Patient`, `RelatedPerson` or `Practitioner`) and `Task.focus` from the main subject.
- **Deadlines.** `AppealResponseDeadlinePolicy` becomes the federal-floor evaluator inside rule-pack evaluation, so its tested values keep working and packs can only tighten them.
- **Portal.** The appeal record page shows the track, requester and subjects; the "original decision" link resolves any subject, not only claim and authorization prefixes.

## 8. Build order

1. **Case model and migration:** track, case type, requester party, subjects; schema version; Mongo migrator and Cosmos script; contract and event 2.0; FHIR mapping; portal record page.
2. **Rule pack evaluation:** per-State level chains and units; federal floors from the existing policy; Texas, then the other nine States from §5.
   - *Shipped first, read-only:* the ten Medicaid packs as JSON embedded in appeals-service (`RulePacks/medicaid/`), loaded and validated at startup against the federal floor (`RulePackValidator`; a bad pack fails startup), with an evaluator that clamps every decision deadline to the federal ceiling. `GET api/appeals/rule-packs`, `/{state}`, `/{state}/deadlines` and `/{state}/recoupment` expose them. Appeal creation does not use them yet: that waits for the track from step 1, so provider disputes never get member clocks.
   - *Medicare Advantage, 2026-10-23:* `RulePacks/medicare/advantage.json`, national rather than per-State (§5.11), and with it the per-line-of-business floor (§4.1). A State's own pack still wins over the national one, so a D-SNP State addendum would land without a redesign. This is what [cho-service.md](cho-service.md) §8 step 2 needed: a contact classified as a Medicare grievance or appeal now has clocks to run.
   - *Later:* move the packs to reference-data-service (`StateComplianceConfig`) with tenant and product overrides, keeping the same validator.
3. **Intake by track:** the §2 decision at intake (portal, Availity, FHIR, call tracking), with work items for missing waivers, AORs or consent.
4. **Provider complaints** and member grievances without a claim.
5. **Recoupment holds** in claims-service and ar-service (§6).
6. **Reporting:** Insights and audit universes split by track, so provider disputes never land in member universes.

## 9. Open questions

1. **Texas contract values:** the provider complaint and claim appeal windows in the UMCC and UMCM couldn't be read; confirm before the Texas pack ships.
2. **Conflicts to settle:** Colorado's fair hearing window (rule 120 days, member page 60) and Arizona's (contract 90, rule 30).
3. **MA payment amount disputes:** confirm the current CMS Payment Dispute Resolution Contractor and process document.
4. **New contracts:** confirm Illinois against the June 2026 HealthChoice contract and Arizona against the current ACC contract.
5. **Oklahoma:** the statute says a provider "may request a fair hearing" while OAC 317:2-3-13 provides none (legal review); confirm the contract's expedited appeal hours.
6. **Michigan:** member notices show 127 days for DIFS external review against 120 in statute; confirm against the current CHCP contract.
7. **Ohio:** confirm the provider agreement values against the current 2026 MCO and MyCare agreements.
8. **Watch list:** Georgia, Nevada and Washington, once their programs' timelines are firm.

## Sources

**Federal**
- [42 CFR 422.214](https://www.ecfr.gov/current/title-42/section-422.214), [422.562](https://www.ecfr.gov/current/title-42/section-422.562), [422.574](https://www.ecfr.gov/current/title-42/section-422.574), [422.578](https://www.ecfr.gov/current/title-42/section-422.578), [422.582](https://www.ecfr.gov/current/title-42/section-422.582), [422.590](https://www.ecfr.gov/current/title-42/section-422.590); [438.402](https://www.ecfr.gov/current/title-42/section-438.402), [438.408](https://www.ecfr.gov/current/title-42/section-438.408), [438.414](https://www.ecfr.gov/current/title-42/section-438.414)
- [CMS Parts C & D Enrollee Grievances, Organization/Coverage Determinations, and Appeals Guidance (eff. July 6, 2026)](https://www.cms.gov/medicare/appeals-and-grievances/mmcag/downloads/parts-c-and-d-enrollee-grievances-organization-coverage-determinations-and-appeals-guidance.pdf)
- [CMS Payment Dispute Resolution process](https://www.cms.gov/Medicare/Health-Plans/HealthPlansGenInfo/downloads/PDRCProcessOct09.pdf)

**Texas:** [Gov't Code ch. 540](https://statutes.capitol.texas.gov/Docs/GV/htm/GV.540.htm); [Gov't Code ch. 544](https://statutes.capitol.texas.gov/Docs/GV/htm/GV.544.htm); [Ins. Code ch. 843](https://statutes.capitol.texas.gov/Docs/IN/htm/IN.843.htm); [1 TAC §353.1454](https://www.law.cornell.edu/regulations/texas/1-Tex-Admin-Code-SS-353-1454); [UMCM 3.21](https://fhb.hhs.texas.gov/sites/default/files/documents/3-21.pdf)

**California:** [DHCS APL 21-011](https://www.dhcs.ca.gov/formsandpubs/Documents/MMCDAPLsandPolicyLetters/APL2021/APL21-011.pdf); [DHCS APL 26-017](https://www.dhcs.ca.gov/file/apl26-017-pdf/); [28 CCR 1300.68](https://www.law.cornell.edu/regulations/california/28-CCR-1300.68); [28 CCR 1300.71](https://www.law.cornell.edu/regulations/california/28-CCR-1300.71); [28 CCR 1300.71.38](https://www.law.cornell.edu/regulations/california/28-CCR-1300.71.38); [H&S Code §1374.30](https://california.public.law/codes/health_and_safety_code_section_1374.30)

**Florida:** [SMMC Attachment II (July 2026)](https://ahca.myflorida.com/file/medicaid/Health_Attachment%20II%20-Core%20Contract%20Provisions_July%202026.pdf); [F.S. §408.7057](http://www.leg.state.fl.us/statutes/index.cfm?App_mode=Display_Statute&URL=0400-0499/0408/Sections/0408.7057.html); [F.S. §641.3155](http://www.leg.state.fl.us/statutes/index.cfm?App_mode=Display_Statute&URL=0600-0699/0641/Sections/0641.3155.html); [Rule 59G-1.100](https://www.law.cornell.edu/regulations/florida/Fla-Admin-Code-Ann-R-59G-1-100)

**New York:** [PHL §4408-a](https://www.nysenate.gov/legislation/laws/PBH/4408-A*2); [PHL §4904](https://www.nysenate.gov/legislation/laws/PBH/4904); [PHL §4910](https://www.nysenate.gov/legislation/laws/PBH/4910); [PHL §4914](https://www.nysenate.gov/legislation/laws/PBH/4914); [Ins. Law §3224-b](https://www.nysenate.gov/legislation/laws/ISC/3224-B); [DOH timeframe comparison](https://healthweb-back.health.ny.gov/health_care/managed_care/plans/appeals/authorize-appeals_timeframe_comparison.htm)

**Colorado:** [10 CCR 2505-10 §8.209](https://regulations.justia.com/states/colorado/2505/2505-d-1/rule-10-ccr-2505-10-8-200/section-10-ccr-2505-10-8-209/); [§8.050](https://regulations.justia.com/states/colorado/2505/2505-d-1/rule-10-ccr-2505-10-8-000/section-10-ccr-2505-10-8-050/)

**Arizona:** [A.R.S. §36-2903.01](https://www.azleg.gov/ars/36/02903-01.htm); [A.A.C. R9-34-405](https://www.law.cornell.edu/regulations/arizona/Ariz-Admin-Code-SS-R9-34-405); [ACOM 412](https://www.azahcccs.gov/shared/Downloads/ACOM/PolicyFiles/400/412.pdf); [ACOM 414](https://www.azahcccs.gov/shared/Downloads/ACOM/PolicyFiles/400/414.pdf); [ACC-RBHA contract amendment (Att. F1/F2)](https://azahcccs.gov/Resources/Downloads/ContractAmendments/ACC-RBHA/ACC-RBHA_T19-21_AZCH-3_CARE1ST-3,MC-3-EFF100123.pdf)

**Illinois:** [305 ILCS 5/5-30.1](https://www.ilga.gov/Documents/legislation/ilcs/documents/030500050K5-30.1.htm); [89 IAC 140.75](https://ilga.gov/ftp/JCAR/AdminCode/089/089001400B00750R.html); [215 ILCS 5/368d](https://www.ilga.gov/Documents/legislation/ilcs/documents/021500050K368d.htm); [HealthChoice Illinois contract model (Aug 2025)](https://hfs.illinois.gov/content/dam/soi/en/web/hfs/medicalproviders/cc/managedcarecontracts/hcicontractmodel.docx)

**Oklahoma:** [OAC 317:2-3-5](https://oklahoma.gov/ohca/policies-and-rules/xpolicy/grievance-procedures-and-process/subchapter-three-member-grievances-and-appeals-provider-complaints-and-state-fair-hearings-in-soonerselect/member-appeals.html); [OAC 317:2-3-10](https://oklahoma.gov/ohca/policies-and-rules/xpolicy/grievance-procedures-and-process/subchapter-three-member-grievances-and-appeals-provider-complaints-and-state-fair-hearings-in-soonerselect/provider-complaint-system.html); [56 O.S. §4002.7](https://law.justia.com/codes/oklahoma/title-56/section-56-4002-7/); [56 O.S. §4002.8](https://law.justia.com/codes/oklahoma/title-56/section-56-4002-8/); [SB 1645 (2026) enrolled](https://www.oklegislature.gov/cf_pdf/2025-26%20ENR/SB/SB1645%20ENR.PDF)

**Michigan:** [CHCP contract (FY24, updated 6-2024)](https://michigan.gov/mdhhs/-/media/Project/Websites/mdhhs/Assistance-Programs/Medicaid-BPHASA/Other-Prov-Specific-Page-Docs/FY24-CHCP-FINAL-Contract-Updated-6-2024.pdf); [MCL 400.111i](https://www.legislature.mi.gov/Laws/MCL?objectName=mcl-400-111i); [MCL 550.1911](https://www.legislature.mi.gov/Laws/MCL?objectName=mcl-550-1911); [MDHHS Rapid Dispute Resolution Process](https://www.michigan.gov/documents/RAPID_DISPUTE_RESOLUTION_PROCESS_88671_7.pdf)

**Ohio:** [OAC 5160-26-08.4](https://codes.ohio.gov/ohio-administrative-code/rule-5160-26-08.4); [OAC 5160-58-08.4](https://codes.ohio.gov/ohio-administrative-code/rule-5160-58-08.4); [ORC 5167.22](https://codes.ohio.gov/ohio-revised-code/section-5167.22); [ODM MCO Provider Agreement (rev. 7/2024)](https://dam.assets.ohio.gov/image/upload/medicaid.ohio.gov/Providers/ProviderTypes/Managed%20Care/Provider%20Agreements/2024_07_MCO_Final.pdf); [ODM External Medical Review FAQ](https://dam.assets.ohio.gov/image/upload/medicaid.ohio.gov/Providers/ProviderTypes/Managed%20Care/External-Medical-Review-FAQ.pdf)
