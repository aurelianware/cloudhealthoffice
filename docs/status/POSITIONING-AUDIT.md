# Cloud Health Office — Positioning & Documentation Audit

**Scope:** Every Cloud Health Office (CHO) documentation and marketing artifact, measured against [`docs/POSITIONING.md`](../POSITIONING.md) (created this session).

**Status:** Audit complete. Phase 2 (separate PR) will execute the updates this audit recommends.

**Method:** Six parallel audit passes covering top-level docs, `docs/features/`, `docs/architecture/` + `docs/api/` + `docs/deployment/` + `docs/guides/`, `docs/sales-materials/`, `docs/roadmap/` + `docs/releases/` + `docs/fundraising/`, and the public site under `src/site/`. A small number of per-service architecture docs and per-service-guide HTML pages were spot-checked rather than fully read; those are flagged ASSUMED-ALIGNED in the full list and should be re-opened if Phase 2 uncovers a surprise.

## Summary

- Total artifacts classified: ~175. The majority were fully read; a minority in low-risk neighborhoods (some per-service architecture internals, most `docs/deployment/*` runbooks, most `src/site/docs/*.html` developer pages) were spot-checked and flagged ASSUMED-ALIGNED.
- ALIGNED: ~95
- UNDERCLAIMS: 22
- OVERCLAIMS: 9
- STALE — UPDATE: 24
- STALE — DEPRECATE: 4
- MIS-LAYERED: 6
- MISSING: 5 (proposed — see MISSING section)

Urgency breakdown of findings that need change:

- P0 (public-facing / evaluator-critical): 22
- P1 (contributor / partner visible): 36
- P2 (internal / low-traffic): 12

## Biggest positioning gaps

### Overclaims — must soften before next evaluator / investor engagement

1. **`docs/fundraising/INVESTOR-ONE-PAGER.md`** — frames four FHIR APIs as "Production Ready" with $1.8M ARR Year-1 targets, no disclosure that there is no production customer yet. Phase 2 must add the pre-pilot disclosure block from POSITIONING.md Layer 3 honest-today section, and relabel "Production Ready" → "Production Ready (Layer 1 / Layer 2 appeals)".
2. **`docs/fundraising/INVESTOR-MEETING-SCRIPT.md`** — claims "100% compliant… in under 5 minutes" and walks through demo as if it were a production deployment at a payer. Phase 2 must qualify the "< 5 minutes" claim to Layer 1 compliance scope only and add a Q&A entry that answers "Are you in production with any customers?" honestly.
3. **`docs/sales-materials/pitch-deck-v4.md`** — Slide 11 ("Trusted by Forward-Thinking Health Plans") lists specific case-study outcomes ("80% reduction in support calls", "99.8% transaction success rate", "deployed in 4 days") as if real. These are template placeholders shown to prospects. Phase 2 must replace Slide 11 wholesale with a "Beta launch — onboarding first pilot partners" frame plus the appeals re-foundation as the qualifying proof point.
4. **`docs/sales-materials/PITCH-DECK-CONTENT.md`** — Slide 4 capability table states "Production Ready" for Patient Access / Provider Access / Prior Auth / Payer-to-Payer without the pilot caveat. Same fix pattern as INVESTOR-ONE-PAGER.
5. **`docs/sales-materials/SALES-PRODUCT-OVERVIEW.md`** — line 8 positions CHO as "complete CMS-0057-F compliance" end-to-end, implying both Layer 1 surface and Layer 3 adjudication are ready. Phase 2 must split the "Key Capabilities" list into Layer 1 (done), Layer 2 (appeals done, other domains in flight), Layer 3 (architecturally complete, gaps per POSITIONING.md).
6. **`docs/releases/v3.0.0-features-overview.md`** — claims FHIR R5 support, AI-powered claim analytics, "150+ payer integrations", Medicare Advantage integration, and "Real-Time Eligibility 3x faster than v2" as Complete (✅) on v3.0.0. None of these are supported by POSITIONING.md evidence. Phase 2: either deprecate (move to `docs/archive/`) or rewrite to match what actually shipped.
7. **`docs/releases/RELEASE_NOTES_v3.0.0.md`** — similar vision-vs-shipped confusion for ClaimRiskScorer, compliance-dashboard, migration-wizard. Phase 2: retitle as "v3.0.0 Release — Historical Reference (December 2025)" and remove forward-looking items that read as shipped.
8. **`docs/announcements/v3.0.0-announcement.md`** — frames v3.0.0 as delivering "99.99% uptime SLA", "Predictive Analytics", "AI-powered insights", "FHIR R5 Support". Phase 2: remove claims not in POSITIONING.md (FHIR R5, predictive analytics) and add Layer-3-is-aspirational note.
9. **`src/site/login.html`** — hero copy "Trusted by Healthcare Organizations" is a soft overclaim given no production reference customer. Phase 2: change to "Built for Healthcare Organizations" (or similar language that signals design-for rather than deployed-with).

### Underclaims — missing revenue / expansion opportunities

The single strongest pattern across sales and marketing material is the absence of the appeals four-PR sequence as the Layer 2 proof point. The positioning story — "start with compliance, then modernize one domain at a time; appeals is already done" — is in POSITIONING.md but in virtually none of the customer-facing artifacts. Fixing the four files below would change the Layer 1 → Layer 2 → Layer 3 revenue arc materially.

1. **`src/site/index.html`** — hero leads with Layer 1 only. Phase 2 must add a "Choose your entry point" section naming the three layers, with the appeals work named as the Layer 2 proof.
2. **`src/site/platform.html`** — scoped to Layer 1 augment mode. Phase 2 must either rename it (`platform-layer1-augment.html`) or broaden it to cover all three layers with appeals as the Layer 2 anchor.
3. **`src/site/solutions-payers.html`** — payer-facing conversion page. Has a case study for Layer 1 compliance but no "What's next? Progressive modernization by domain" section referencing PRs #677/#678/#680/#681. Phase 2: add that section.
4. **`docs/sales-materials/MARKETING-LANDING-PAGE-COPY.md`** — frames CHO purely as a compliance add-on. Phase 2: add a Value Prop 5 on progressive modernization plus an FAQ on the Layer 1 → Layer 2 path.
5. **`docs/sales-materials/sales-proposal-template.md`** — P0 customer proposal template. Has no "Strategic Roadmap: from compliance to modernization" section. Phase 2: add it (Year 1 = Layer 1 compliance, Year 2 = appeals Layer 2, Year 3 = expansion).
6. **`docs/sales-materials/demo-materials/demo-script.md`** — 30-minute customer demo ends at ROI with no pivot to "here's how this scales into your modernization story." Phase 2: add a final 3-minute Part 8 on Layer 2.
7. **`docs/sales-materials/outreach-campaigns/cold-call-scripts.md`** and **`email-templates.md`** — all 10 templates each are Layer-1-only. Phase 2: add Layer 2 hook to the decision-maker scripts and to cold email templates 1-2.
8. **`docs/features/ROADMAP.md`**, **`docs/features/ROADMAP-2026.md`**, **`docs/features/V4-LAUNCH-ROADMAP.md`** — none explicitly sequence work by the three layers, and none credit the appeals re-foundation as shipped.
9. **`README.md`** (repo root) — describes CHO in Layer-1-plus-augment terms but never names the three-layer model and under-counts the service inventory (see cross-cutting patterns).

### Missing critical artifacts

See the MISSING section below. Most consequential: `docs/adr/006-three-layer-positioning-model.md` (to pin this decision in the ADR log) and `docs/deployment/APPEALS-RUNBOOK.md` (the operational accompaniment to the re-foundation work that isn't yet written).

## Full artifact classification

### P0 — public-facing / evaluator- / investor-critical

#### `README.md` (repo root)
- **Layer:** multi
- **State:** UNDERCLAIMS
- **Current framing:** Describes CHO as a compliance surface coexisting with QNXT/HealthEdge/Facets, multi-cloud deployable, lists service count as "29 microservices."
- **Positioning verdict:** Service count is wrong (actual is 36 under `src/services/`; see POSITIONING.md Layer 3 evidence). The README also does not name the three-layer model and does not credit the appeals four-PR sequence as the Layer 2 proof point.
- **Proposed change:** Update the service-count line to "36 services, 9 engines" (consistent with POSITIONING.md). Add a "How CHO engages: three layers" paragraph after the intro, summarizing POSITIONING.md §Summary. Add a "Latest milestone" sub-section naming PRs #677, #678, #680, #681 as the shipped Layer 2 proof point.
- **Scope estimate:** small

#### `CHANGELOG.md` (repo root)
- **Layer:** multi
- **State:** STALE-UPDATE
- **Current framing:** Recent release entries describe v3.0.0 / v4.0.0 / v4.2.0 / v4.3.0 features but do not single out the appeals re-foundation PRs.
- **Positioning verdict:** The appeals sequence is CHO's Layer 2 lighthouse; the CHANGELOG should say so explicitly on the relevant release, since this is the first place contributors check when the question is "when did appeals ship."
- **Proposed change:** Add a sub-entry under the v4.x section that shipped appeals: "appeals re-foundation: four-PR sequence (#677 FHIR profiles, #678 modernized service, #680 FHIR façade, #681 X12 275 Kafka consumer) — Layer 2 proof point per POSITIONING.md."
- **Scope estimate:** small

#### `docs/decisions/adr-031-compliance-config-auth.md`
- **Layer:** 1
- **State:** UNDERCLAIMS
- **Current framing:** Pending ADR identifying that `PUT /api/compliance-config/{tenantId}` lost its AdminPolicy in PR #634 and is currently unprotected at the controller level.
- **Positioning verdict:** This is a Layer 1 blocker — Layer 1 is sold as a compliance surface deployed inside payer tenants. An unprotected admin-config endpoint is not safe for external tenant access. The ADR correctly identifies the gap but lists it as "Pending" without escalation.
- **Proposed change:** Change status line to "Accepted — blocking for Layer 1 production release." Add a closing sentence explicitly citing Layer 1 as the affected commercial path.
- **Scope estimate:** small

#### `docs/announcements/v3.0.0-announcement.md`
- **Layer:** multi
- **State:** OVERCLAIMS
- **Current framing:** "Open Frontier Release" — pitches v3.0.0 as delivering multi-cloud independence, predictive analytics, AI-powered insights, FHIR R5, "99.99% uptime SLA."
- **Positioning verdict:** Several items (FHIR R5, predictive analytics, 99.99% SLA) are not in POSITIONING.md evidence. The v3.0.0 announcement is the public-facing framing of a major release; these claims set expectations that Layer 3 breadth is proven production behavior.
- **Proposed change:** Remove FHIR R5, predictive analytics, and 99.99% SLA claims. Add a closing note: "v3.0 ships Layer 1 (compliance) and Layer 2 (appeals re-foundation) production-ready. Layer 3 (full CAPS) is architecturally complete; see POSITIONING.md for the honest today state."
- **Scope estimate:** medium

#### `docs/sales-materials/PITCH-DECK-CONTENT.md`
- **Layer:** multi
- **State:** OVERCLAIMS + UNDERCLAIMS
- **Current framing:** Slide 4 states "Production Ready" for Patient Access, Provider Access, Prior Auth, Payer-to-Payer APIs without pilot caveat. Deck never explicitly names the three layers or credits the appeals four-PR sequence.
- **Positioning verdict:** Both failure modes at once — Layer 3 is overclaimed, Layer 2 is underclaimed, Layer 1 scope is conflated with full platform scope.
- **Proposed change:** Slide 4: add footnote "*Production-ready Layer 1 surface; first pilot deployment in motion, no production reference customer yet (POSITIONING.md §Layer 3 honest-today)." Add a new slide after Slide 4 titled "Layer 2 proof: appeals shipped" summarizing the four-PR sequence.
- **Scope estimate:** medium

#### `docs/sales-materials/pitch-deck-v4.md`
- **Layer:** multi
- **State:** OVERCLAIMS + UNDERCLAIMS
- **Current framing:** "Ready for production" subtitle (line 33); "100% Guaranteed" on line 117; Slide 11 (lines 481-509) lists fake case-study outcomes with specific numbers.
- **Positioning verdict:** Slide 11 is the single most problematic artifact in the audit — it shows specific production metrics attributed to customers that do not exist. Line 117's "100% Guaranteed" is not a claim any pre-pilot platform can defensibly make.
- **Proposed change:** Slide 11 (lines 481-509): replace in full with a "Beta partners — first pilots in motion" frame, naming the appeals re-foundation (PRs #677-#681) as the qualifying proof. Line 33: "Ready for production" → "Production-ready for new entrants; Layer 2 pathway for established payers." Line 117: "100% Guaranteed" → "100% CMS-0057-F capability coverage; pilot-validated with first partners."
- **Scope estimate:** medium

#### `docs/sales-materials/SALES-PRODUCT-OVERVIEW.md`
- **Layer:** multi
- **State:** OVERCLAIMS
- **Current framing:** Line 8: "Industry's first source-available, Azure-native EDI platform delivering complete CMS-0057-F compliance." "Key Capabilities" section lists all EDI transaction types with no qualifier on adapter maturity.
- **Positioning verdict:** Conflates Layer 1 compliance surface with the Layer 3 full platform. IFhirDataAdapter is still mock for several domains per POSITIONING.md; presenting 837/835/278 mapping as equivalent to the Layer 1 CMS-0057-F surface is the overclaim.
- **Proposed change:** Rewrite line 8 to split the sentence: Layer 1 compliance surface (production-ready) and Layer 2 progressive modernization (appeals shipped, other domains in flight). Add a "Maturity by layer" table as described in POSITIONING.md §Layer 3 honest-today.
- **Scope estimate:** medium

#### `docs/sales-materials/MARKETING-LANDING-PAGE-COPY.md`
- **Layer:** 1
- **State:** UNDERCLAIMS
- **Current framing:** "CMS-0057-F Compliance in 5 Minutes" + four value props, all Layer 1.
- **Positioning verdict:** Positions CHO as a compliance add-on and leaves the Layer 2 → Layer 3 story untouched. Buyer who reads only this page does not know modernization or full-platform pathways exist.
- **Proposed change:** Add Value Prop 5: "Progressive Modernization Path" with the strangler-fig one-liner. Add an FAQ entry: "Can we start with just compliance and migrate later? Yes — Layer 1 stands alone; Layer 2 begins with appeals (already shipped, see PRs #677-#681)."
- **Scope estimate:** small

#### `docs/sales-materials/demo-materials/demo-script.md`
- **Layer:** 1
- **State:** UNDERCLAIMS
- **Current framing:** 30-minute demo ending at ROI calculator. No transition to modernization roadmap.
- **Positioning verdict:** The demo converts a Layer 1 sale but leaves the Layer 2 / Layer 3 conversation unopened.
- **Proposed change:** Insert a new ~3-minute Part 8 before Q&A titled "The Next Chapter: Progressive Modernization." Name appeals as the Layer 2 proof point and propose a post-go-live modernization-roadmap discussion.
- **Scope estimate:** small

#### `docs/sales-materials/proposals/sales-proposal-template.md`
- **Layer:** multi
- **State:** UNDERCLAIMS
- **Current framing:** Proposal template scoped entirely to Layer 1 attachment automation and compliance.
- **Positioning verdict:** For a P0 customer proposal template, missing the Layer 2 / Layer 3 expansion path means every proposal sent will cap at a Layer 1 ACV.
- **Proposed change:** Add a new section after "Proposed Solution" titled "Strategic Roadmap: from compliance to modernization" with Year 1 / Year 2-3 / Year 3+ phasing as described in POSITIONING.md §How the three layers fit together.
- **Scope estimate:** medium

#### `docs/sales-materials/PHASE1-SUMMARY.md`
- **Layer:** 1
- **State:** ALIGNED
- **Current framing:** Internal summary of Phase-1 sales material readiness.
- **Positioning verdict:** Accurate; doesn't overclaim. Internal-facing.
- **Proposed change:** No change needed.
- **Scope estimate:** —

#### `docs/sales-materials/README.md`
- **Layer:** multi
- **State:** ALIGNED
- **Current framing:** Index of sales materials with usage instructions.
- **Proposed change:** No change needed.
- **Scope estimate:** —

#### `docs/fundraising/INVESTOR-ONE-PAGER.md`
- **Layer:** multi
- **State:** OVERCLAIMS
- **Current framing:** "Production Ready" labels on four APIs, $1.8M ARR / 50-customer Year-1 target framed alongside feature readiness.
- **Positioning verdict:** Investor-facing document with no pre-pilot disclosure. Presents pilot targets in the same visual register as shipped capabilities.
- **Proposed change:** Add prominent "Pre-Pilot Status" box at the top of the one-pager lifting the POSITIONING.md Layer 3 honest-today paragraph verbatim. Change "Production Ready" badges to "Production Ready (Layer 1 / appeals)." Move revenue numbers into a clearly-labeled "Targets" block.
- **Scope estimate:** medium

#### `docs/fundraising/INVESTOR-MEETING-SCRIPT.md`
- **Layer:** multi
- **State:** OVERCLAIMS
- **Current framing:** Script for 30-minute investor meeting claiming "100% compliant" and "under 5 minutes" deployment, with financial projections framed as likely outcomes.
- **Positioning verdict:** Same overclaim pattern as the one-pager, amplified by verbal delivery context.
- **Proposed change:** Qualify the "<5 minutes" / "100% compliant" claim to Layer 1 compliance scope. Add a Q&A block to the appendix with the question "Are you in production with any customers?" and the answer from POSITIONING.md §Layer 3 honest-today.
- **Scope estimate:** medium

#### `docs/fundraising/README.md`
- **Layer:** multi
- **State:** ALIGNED (mostly)
- **Current framing:** Index of fundraising materials with readiness checklist.
- **Positioning verdict:** Aligned at index level; the problems are in the linked documents, not this README.
- **Proposed change:** Add a one-line disclaimer at the top: "All fundraising materials must derive from POSITIONING.md; overclaims found in this audit (see `docs/status/POSITIONING-AUDIT.md`) must be resolved before sending to investors."
- **Scope estimate:** small

#### `docs/fundraising/DUE-DILIGENCE-CHECKLIST.md`
- **Layer:** multi
- **State:** ALIGNED with gaps
- **Current framing:** DD checklist covering legal, financial, technical, commercial, team.
- **Positioning verdict:** Doesn't surface the specific honest gaps from POSITIONING.md §Layer 3 honest-today as DD line-items — making it easier for a fundraising conversation to miss them.
- **Proposed change:** Add a "Critical Disclosures" section with the explicit gaps: no production reference customer, claims/provider/sponsor coverage at stated levels, IFhirDataAdapter mostly mock except appeals, no correspondence-service, no top-tier-payer scale test.
- **Scope estimate:** small

#### `docs/implementation/SECURITY-FIXES-SUMMARY.md`
- **Layer:** not-layer-specific
- **State:** ALIGNED
- **Current framing:** v4.0.0 security hardening summary.
- **Proposed change:** No change needed.
- **Scope estimate:** —

#### `src/site/index.html`
- **Layer:** 1 (should be multi)
- **State:** UNDERCLAIMS
- **Current framing:** Hero leads with "CMS-0057-F compliance without replacing your core admin system."
- **Positioning verdict:** Layer 1 entry point is correctly surfaced; the Layer 2 / Layer 3 entry points are not.
- **Proposed change:** Add a "Choose your entry point" section immediately below the hero, naming the three layers with one-line descriptions and links, matching POSITIONING.md §Summary.
- **Scope estimate:** small

#### `src/site/platform.html`
- **Layer:** 1 (should be multi)
- **State:** MIS-LAYERED + UNDERCLAIMS
- **Current framing:** "Compliance First. Modernization on Your Terms" — the whole page is scoped to Layer 1 augment mode.
- **Positioning verdict:** A page titled "platform" reading as Layer 1 only is the strongest driver of the "CHO is a compliance add-on" perception.
- **Proposed change:** Either rename to `platform-layer1-augment.html` and create a new `platform.html` that spans the three layers, or refactor the existing page to have a three-layer tour with Layer 1 as the opening section and appeals as the Layer 2 anchor.
- **Scope estimate:** medium

#### `src/site/cms-0057f-compliance.html`
- **Layer:** 1
- **State:** ALIGNED
- **Proposed change:** No change needed (correctly scoped to Layer 1).
- **Scope estimate:** —

#### `src/site/pricing.html`
- **Layer:** multi
- **State:** ALIGNED
- **Positioning verdict:** Pricing page already maps Starter / Professional / Enterprise to layers (adapter status labels reflect maturity honestly). This is the strongest-aligned public page in the audit.
- **Proposed change:** No change needed.
- **Scope estimate:** —

#### `src/site/solutions-payers.html`
- **Layer:** 1
- **State:** UNDERCLAIMS + STALE-UPDATE
- **Current framing:** "Meet CMS-0057-F Without Replacing Your Core Admin System" + QNXT cost case study.
- **Positioning verdict:** Payer-facing conversion page without any Layer 2 progressive modernization story or appeals proof point.
- **Proposed change:** Add a section after the case study titled "What's next — progressive modernization by domain" citing PRs #677, #678, #680, #681 and the strangler-fig narrative from POSITIONING.md §Layer 2.
- **Scope estimate:** medium

#### `src/site/release-notes.html`
- **Layer:** multi
- **State:** ALIGNED with minor version inconsistency
- **Current framing:** v4.1 release summary including appeals re-foundation.
- **Positioning verdict:** Good. Only issue: footer says "22 microservices / 9 engines" while the body says "29 microservices / 6 calculation engines" and POSITIONING.md says "36 services / 9 engines." Inconsistency.
- **Proposed change:** Reconcile the footer to body, then reconcile both to POSITIONING.md ("36 services, 9 engines").
- **Scope estimate:** small

#### `src/site/insights.html`
- **Layer:** not-layer-specific
- **State:** ALIGNED
- **Proposed change:** No change needed.
- **Scope estimate:** —

#### `src/site/docs/index.html`
- **Layer:** multi
- **State:** ALIGNED
- **Proposed change:** No change needed.
- **Scope estimate:** —

#### `src/site/login.html`
- **Layer:** not-layer-specific
- **State:** OVERCLAIMS (minor)
- **Current framing:** "Trusted by Healthcare Organizations" hero.
- **Positioning verdict:** Soft overclaim — no production reference customer yet per POSITIONING.md.
- **Proposed change:** Change to "Built for Healthcare Organizations" or similar design-for language. Other six value props on the page are accurate.
- **Scope estimate:** small

#### `docs/api/APPEALS-OPENAPI.yaml`
- **Layer:** 2
- **State:** ALIGNED
- **Current framing:** OpenAPI 3 spec for the post-modernization appeals surface.
- **Proposed change:** No change needed.
- **Scope estimate:** —

#### `docs/features/CMS-0057-F-COMPLIANCE.md`
- **Layer:** 1
- **State:** STALE-UPDATE
- **Current framing:** Compliance summary with extensive Logic Apps references in the body even though header notes migration.
- **Proposed change:** Scrub all Logic Apps references from the body, replace with Argo Workflows links to ADR-004. Add a prominent note that IFhirDataAdapter is fully wired only for appeals; other domains still use mock adapters (per POSITIONING.md Layer 3 honest-today).
- **Scope estimate:** medium

#### `docs/features/SAAS-LAUNCH-READINESS.md`
- **Layer:** 3
- **State:** ALIGNED
- **Current framing:** "75% technically ready, 25% business-ready" honest audit.
- **Positioning verdict:** This is already a POSITIONING-aligned document — it validates the honest disclosure pattern.
- **Proposed change:** Add a one-line reference to POSITIONING.md at the top so future readers understand the canonical link.
- **Scope estimate:** small

### P1 — contributor / partner / prospect visible

#### `docs/adr/001-argo-vs-airflow.md`, `docs/adr/002-kafka-vs-nats.md`, `docs/adr/003-pyx12-library.md`, `docs/adr/004-remove-logic-apps.md`
- **Layer:** not-layer-specific
- **State:** ALIGNED
- **Proposed change:** No change needed. These are historical architectural decisions; no Layer-specific statements.
- **Scope estimate:** —

#### `docs/compliance/FL-AHCA-COMPLIANCE.md`
- **Layer:** 1
- **State:** ALIGNED
- **Proposed change:** No change needed. Correctly models per-tenant state-compliance configuration pattern.
- **Scope estimate:** —

#### `docs/engines/ACCUMULATOR-ENGINE.md`, `docs/engines/FEE-SCHEDULE-ENGINE.md`
- **Layer:** 3 (internals)
- **State:** ALIGNED
- **Proposed change:** No change needed.
- **Scope estimate:** —

#### `docs/implementation/PRIOR-AUTH-IMPLEMENTATION-SUMMARY.md`
- **Layer:** 2
- **State:** STALE-UPDATE
- **Current framing:** Leads with Logic Apps mention and pointer to ADR-004.
- **Proposed change:** Rewrite the introduction (lines 1-16) to center Argo. Restate prior auth as a Layer 2 modernization candidate after appeals.
- **Scope estimate:** medium

#### `docs/features/APPEALS-INTEGRATION.md`
- **Layer:** 2
- **State:** ALIGNED (reference implementation)
- **Proposed change:** No change needed.
- **Scope estimate:** —

#### `docs/features/APPEALS-BACKEND-INTERFACE.md`
- **Layer:** 2
- **State:** ALIGNED
- **Proposed change:** No change needed.
- **Scope estimate:** —

#### `docs/features/FHIR-INTEGRATION.md`
- **Layer:** multi
- **State:** STALE-UPDATE
- **Current framing:** Lists 837 → Claim and 835 → EOB as "Production Ready" without qualifying that the adapters are mock outside appeals.
- **Proposed change:** Insert an adapter-status table: 270/271 live, 275 live (appeals), 278 mock, 837 mock, 835 mock. Replace Logic Apps references with Argo.
- **Scope estimate:** medium

#### `docs/features/FHIR-IMPLEMENTATION-SUMMARY.md`
- **Layer:** 3
- **State:** UNDERCLAIMS
- **Current framing:** Document title reads broad; content is scoped to 270/271 eligibility.
- **Proposed change:** Retitle to "FHIR R4 Eligibility Integration — Implementation Summary." Add a pointer to `docs/api/APPEALS-OPENAPI.yaml` for the appeals-adapter equivalent and to the roadmap for claims/EOB/prior-auth adapters.
- **Scope estimate:** small

#### `docs/features/PATIENT-ACCESS-API.md`
- **Layer:** 1
- **State:** STALE-UPDATE
- **Proposed change:** Clarify that Patient and Coverage endpoints are live; ExplanationOfBenefit and Claim return mock data pending the 835/837 adapters. Replace Logic Apps references.
- **Scope estimate:** medium

#### `docs/features/PRIOR-AUTHORIZATION-API.md`
- **Layer:** 1 / 2
- **State:** STALE-UPDATE
- **Proposed change:** Add a layer-breakout at the top of the doc distinguishing Layer 1 read-only prior-auth queries from Layer 2 full domain. Replace Logic Apps references.
- **Scope estimate:** medium

#### `docs/features/AUTHORIZATION-REQUEST.md`, `docs/features/AUTHORIZATION-INQUIRY.md`
- **Layer:** 1 / 2
- **State:** STALE-UPDATE
- **Proposed change:** Replace Logic Apps references with Argo links (ADR-004).
- **Scope estimate:** small each

#### `docs/features/ROADMAP.md`
- **Layer:** multi
- **State:** UNDERCLAIMS
- **Proposed change:** Add a "Three-layer roadmap" framing at the top. Move Q1 / Q2 sections into layer-specific columns. Call out appeals re-foundation as completed Layer 2 work.
- **Scope estimate:** medium

#### `docs/features/ROADMAP-2026.md`
- **Layer:** multi
- **State:** UNDERCLAIMS
- **Proposed change:** Add a Layer 1 / Layer 2 / Layer 3 framing to the executive summary. Reclassify the "Eligibility Microservice v2" line as an existing-service hardening item rather than a new release.
- **Scope estimate:** medium

#### `docs/features/V4-LAUNCH-ROADMAP.md`
- **Layer:** 3
- **State:** OVERCLAIMS
- **Current framing:** v4.0 described as "production-ready SaaS platform" launching in 8-12 weeks.
- **Positioning verdict:** Conflicts with `SAAS-LAUNCH-READINESS.md` (same repo) which honestly flags the gaps. Resolve by reading v4.0 as a Layer 1 + Layer 2 milestone, not full Layer 3 GA.
- **Proposed change:** Add a pointer to `SAAS-LAUNCH-READINESS.md` and re-scope the v4.0 launch to Layer 1 + Layer 2 (appeals). Defer Layer 3 commercial launch to a future version with a named pilot partner.
- **Scope estimate:** medium

#### `docs/features/RELEASE-v4.0.0.md`
- **Layer:** 3
- **State:** UNDERCLAIMS
- **Proposed change:** Add a "Positioning context" section stating that v4.0 is focused on Layer 1 / Layer 2 readiness. Link to POSITIONING.md.
- **Scope estimate:** small

#### `docs/features/WHATS-NEW.md`
- **Layer:** multi
- **State:** STALE-UPDATE
- **Proposed change:** Add a three-layer preamble. Remove "New in This Release" labels for services that pre-existed v3.0.
- **Scope estimate:** medium

#### `docs/features/UPDATES-SUMMARY.md`
- **Layer:** multi
- **State:** STALE-UPDATE
- **Proposed change:** Reconcile ship-date conflicts with `V4-LAUNCH-ROADMAP.md`. Explicitly state v4 scope as Layer 1 + Layer 2 (not Layer 3).
- **Scope estimate:** small

#### `docs/features/MICROSERVICES-IMPLEMENTATION-STATUS.md`
- **Layer:** 3
- **State:** STALE-UPDATE
- **Proposed change:** Refresh service counts against the `src/services/` inventory (36, not 22). Add a test-coverage column that surfaces the claims/provider/sponsor coverage disclosed in POSITIONING.md.
- **Scope estimate:** small

#### `docs/features/IMPLEMENTATION-SUMMARY.md`
- **Layer:** multi
- **State:** STALE-UPDATE
- **Proposed change:** Add a framing paragraph linking the Config-to-Workflow Generator to the three layers (it enables all three).
- **Scope estimate:** small

#### `docs/features/KUBERNETES-MICROSERVICES-ARCHITECTURE.md`
- **Layer:** 3
- **State:** UNDERCLAIMS
- **Proposed change:** Add a layer-framing paragraph tying the Kubernetes architecture to POSITIONING.md Layer 3 evidence. Clarify that Layer 1 uses a subset of the services, Layer 2 adds domain services per tenant.
- **Scope estimate:** small

#### `docs/features/RELEASE_NOTES.md`
- **Layer:** multi
- **State:** STALE-UPDATE
- **Proposed change:** Add per-release Layer 1 / Layer 2 / Layer 3 annotations. Call out appeals re-foundation in the relevant release block.
- **Scope estimate:** small

#### `docs/features/MULTI-TENANT-SAAS-ARCHITECTURE.md`, `docs/features/EDI-WORKFLOWS-COMPLETE.md`, `docs/features/ARGO-MIGRATION-GUIDE.md`, `docs/features/ARGO-OPERATIONS.md`, `docs/features/ARGO-WORKFLOWS-MULTI-TENANT-UPDATE.md`, `docs/features/AUTHORIZATION-ATTACHMENTS-ARCHITECTURE.md`, `docs/features/BACKEND-INTERFACE.md`, `docs/features/FHIR-SECURITY-NOTES.md`, `docs/features/HIPAA-COMPLIANCE-MATRIX.md`, `docs/features/FL-AHCA-SMMC-COMPLIANCE.md`, `docs/features/CONFIG-TO-WORKFLOW-GENERATOR.md`, `docs/features/ECS-INTEGRATION.md`, `docs/features/HIPAA-X12-Agreements-Guide.md`, `docs/features/834-*.md`, `docs/features/837-CLAIMS-PIPELINE.md`, `docs/features/276-277-IMPLEMENTATION-COMPLETE.md`, `docs/features/VALUEADDS277-*.md`
- **Layer:** varies (mostly not-layer-specific or scoped to the right layer)
- **State:** ALIGNED
- **Proposed change:** No change needed.
- **Scope estimate:** —

#### `docs/features/ADMIN-CONSENT-SETUP.md`, `docs/features/AI-ERROR-RESOLUTION.md`, `docs/features/AI-RESOLUTION-QUICKSTART.md`, `docs/features/AKS-CLUSTER-SETUP.md`, `docs/features/AZURE-MONITOR-DASHBOARDS.md`, `docs/features/BRANDING-GUIDELINES.md`, `docs/features/COMMERCIALIZATION.md`, `docs/features/DEPLOYMENT-PIPELINE-FIX-SUMMARY.md`, `docs/features/FEDERATED-CREDENTIALS-SETUP.md`, `docs/features/FIX-AADSTS1003031.md`, `docs/features/GATED-RELEASE-IMPLEMENTATION-SUMMARY.md`, `docs/features/GITHUB-ACTIONS-SETUP.md`, `docs/features/HIPAA-AUDIT-REPORT.md`, `docs/features/KEY-VAULT-AUTO-CONFIGURATION.md`, `docs/features/MONITORING-AND-OBSERVABILITY.md`, `docs/features/MONITORING-IMPLEMENTATION-SUMMARY.md`, `docs/features/MULTI-CLOUD-DEPLOYMENT.md`, `docs/features/MULTI-TENANT-ENVIRONMENT-STRUCTURE.md`, `docs/features/OAUTH2-AUTHENTICATION-SETUP.md`, `docs/features/ONBOARDING-CONFIGURATION-WORKSHEET.md`, `docs/features/PHI-SCANNER-GUIDE.md`, `docs/features/PRIOR-AUTH-IMPLEMENTATION-SUMMARY.md`, `docs/features/PRODUCTION-DEPLOYMENT-GUIDE.md`, `docs/features/WEBSITE-PHASE2-COMPLETE.md`, `docs/features/WEBSITE-UPDATES-FINAL.md`, `docs/features/managed-services-matrix.md`, `docs/features/questionnaires/qre-*.md`
- **Layer:** operational / non-positioning
- **State:** ALIGNED
- **Proposed change:** No change needed.
- **Scope estimate:** —

#### `docs/architecture/ARCHITECTURE.md`
- **Layer:** multi
- **State:** ALIGNED
- **Proposed change:** No change needed (cites Argo / ADR-004 correctly; no Layer-1-only framing).
- **Scope estimate:** —

#### `docs/architecture/OPERATING-MODE.md`
- **Layer:** 2
- **State:** ALIGNED (Layer 2 anchor doc)
- **Proposed change:** No change needed.
- **Scope estimate:** —

#### `docs/architecture/ADJUDICATION-PIPELINE.md`
- **Layer:** 3
- **State:** ALIGNED
- **Proposed change:** No change needed (documents Argo pipeline and 9 engines accurately).
- **Scope estimate:** —

#### `docs/architecture/accumulator-service.md`, `docs/architecture/benefits-viewer.md`, `docs/architecture/CLAIMS-EXAMINER-SERVICE.md`, `docs/architecture/member-foundation.md`, `docs/architecture/member-linkage-tabs.md`, `docs/architecture/idcard-service.md`, `docs/architecture/observability.md`, `docs/architecture/shared-cache.md`, `docs/architecture/shared-json-options.md`, `docs/architecture/shared-messagebus.md`, `docs/architecture/temporal-eligibility.md`, `docs/architecture/pcp-assignment.md`, `docs/architecture/member-alerts-notes.md`, `docs/architecture/secret-rotation.md`, `docs/architecture/SFTP-ARCHITECTURE.md`, `docs/architecture/BRANCHING-STRATEGY.md`, `docs/architecture/BRANDING-IMPLEMENTATION-SUMMARY.md`
- **Layer:** not-layer-specific or Layer 3 internals
- **State:** ALIGNED (spot-checked; no positioning-level claims)
- **Proposed change:** No change needed.
- **Scope estimate:** —

#### `docs/architecture/SFTP-MULTI-TENANT-ARCHITECTURE.md`
- **Layer:** not-layer-specific
- **State:** ALIGNED — with an honest disclosure that current single-user SFTP is a multi-tenancy gap
- **Proposed change:** No change needed (the doc correctly flags the gap as a proposed fix).
- **Scope estimate:** —

#### `docs/guides/ARCHITECTURE.md`, `docs/guides/DEPLOYMENT.md`, `docs/guides/FEATURES.md`, `docs/guides/QUICK-UPDATE-GUIDE.md`, `docs/guides/QUICKSTART.md`
- **Layer:** multi / not-layer-specific
- **State:** ALIGNED
- **Proposed change:** No change needed.
- **Scope estimate:** —

#### `docs/sales-materials/PILOT-PROGRAM.md`
- **Layer:** 1 / 2
- **State:** ALIGNED with minor gap
- **Proposed change:** Add a "Modernization Interest" selection criterion (5%) and a post-pilot "growth roadmap" section naming appeals as the Layer 2 first step.
- **Scope estimate:** small

#### `docs/sales-materials/FINANCIAL-MODEL.md`
- **Layer:** multi
- **State:** ALIGNED with clarification needed
- **Proposed change:** Add a "Pricing by layer" table matching POSITIONING.md §Commercial shape in each layer.
- **Scope estimate:** small

#### `docs/sales-materials/SALES-CASE-STUDY-TEMPLATE.md`
- **Layer:** n/a (template)
- **State:** ALIGNED
- **Proposed change:** Add a guardrail instruction at the top: "Do not populate with projected or hypothetical data; Layer 3 case studies require a production deployment."
- **Scope estimate:** small

#### `docs/sales-materials/SALES-EMAIL-TEMPLATES.md`
- **Layer:** 1
- **State:** UNDERCLAIMS
- **Proposed change:** Add a Layer 2 sentence to templates 1 and 5 (after the core proof points), introducing progressive modernization as the next chapter.
- **Scope estimate:** small

#### `docs/sales-materials/SALES-ROI-CALCULATOR.md`
- **Layer:** 1
- **State:** ALIGNED (honest about projections)
- **Proposed change:** Add a short "Layer 2 / Layer 3 ROI" section pointing to domain-specific modeling upon request.
- **Scope estimate:** small

#### `docs/sales-materials/ADJUDICATION-OVERVIEW.md`
- **Layer:** 2 / 3
- **State:** STALE-UPDATE
- **Proposed change:** Add a "Maturity by domain" section reflecting POSITIONING.md Layer 3 honest-today. Qualify the AI Claims Examiner feature as advisory-only and tied to claims-service (still hardening).
- **Scope estimate:** small

#### `docs/sales-materials/contracts/master-services-agreement-template.md`
- **Layer:** multi
- **State:** ALIGNED
- **Proposed change:** Add a definition for "Operating Mode" (Augment / Replace / Legacy) in the Definitions section, to make Layer 2 expansion legible in the legal doc.
- **Scope estimate:** small

#### `docs/sales-materials/contracts/order-form-template.md`
- **Layer:** multi
- **State:** ALIGNED
- **Proposed change:** Add a tier-to-layer mapping section.
- **Scope estimate:** small

#### `docs/sales-materials/deployment-guides/customer-onboarding-checklist.md`
- **Layer:** 1
- **State:** ALIGNED
- **Proposed change:** Add a Week-2+ section on modernization planning (appeals as first candidate domain).
- **Scope estimate:** small

#### `docs/sales-materials/deployment-guides/manual-customer-deployment-guide.md`
- **Layer:** 1
- **State:** ALIGNED
- **Proposed change:** No change needed.
- **Scope estimate:** —

#### `docs/sales-materials/email-templates/welcome-email-beta.md`
- **Layer:** 1
- **State:** ALIGNED
- **Proposed change:** Add a post-Day-7 one-liner on Layer 2 roadmap planning.
- **Scope estimate:** small

#### `docs/sales-materials/outreach-campaigns/cold-call-scripts.md`
- **Layer:** 1
- **State:** UNDERCLAIMS
- **Proposed change:** Add a Layer 2 transition line to the decision-maker script and the objection-handling script.
- **Scope estimate:** small

#### `docs/sales-materials/outreach-campaigns/email-templates.md`
- **Layer:** 1
- **State:** UNDERCLAIMS
- **Proposed change:** Add a Layer 2 paragraph to templates 1-2.
- **Scope estimate:** small

#### `docs/sales-materials/landing-page/index.html`
- **Layer:** 1
- **State:** UNDERCLAIMS
- **Proposed change:** Add Layer 2 value prop and FAQ entry to the HTML equivalent of `MARKETING-LANDING-PAGE-COPY.md`.
- **Scope estimate:** medium

#### `docs/roadmap/CHO-Roadmap-Readme.md`
- **Layer:** multi
- **State:** STALE-UPDATE
- **Proposed change:** Bring the "Last updated" date current. Mark phases that the appeals sequence closed (Phase 1 / Phase 2 of the document's own schema) as Complete, with links to PRs #677-#681.
- **Scope estimate:** small

#### `docs/roadmap/CHO-ENHANCEMENT-CHECKLIST.md`, `docs/roadmap/CHO-ENHANCEMENT-STATUS.md`
- **Layer:** multi
- **State:** ALIGNED
- **Proposed change:** Refresh dates only; no structural change.
- **Scope estimate:** small each

#### `docs/releases/README.md`
- **Layer:** multi
- **State:** STALE-UPDATE
- **Proposed change:** Remove the AI / predictive analytics / "150+ payer integration" claims. Replace with a short pointer to POSITIONING.md and a current-release card (v4.1).
- **Scope estimate:** medium

#### `docs/releases/RELEASE_NOTES_v3.0.0.md`
- **Layer:** 1 (historical)
- **State:** STALE-DEPRECATE
- **Proposed change:** Retitle with "(Historical Reference)" and trim forward-looking items (ClaimRiskScorer, compliance dashboard, migration wizard) that either didn't ship in v3 or are roadmap.
- **Scope estimate:** medium

#### `docs/releases/v3.0.0-features-overview.md`
- **Layer:** 1 / 3
- **State:** OVERCLAIMS
- **Proposed change:** Either deprecate entirely (move to `docs/archive/`) or rewrite to match what actually shipped. Removing FHIR R5, AI-powered analytics, Medicare Advantage integration, "3x faster eligibility", and "150+ payer integrations" is required either way.
- **Scope estimate:** large

#### `docs/releases/v4.1.0-FHIR-API-PROMINENCE.md`
- **Layer:** 1
- **State:** ALIGNED with minor add
- **Proposed change:** Add a row to the before/after comparison explicitly calling out the appeals FHIR profiles as the Layer 2 anchor.
- **Scope estimate:** small

#### `docs/fundraising/PILOT-TO-FUNDING.md`
- **Layer:** multi
- **State:** ALIGNED
- **Proposed change:** Add a "pilot messaging by layer" section distinguishing Layer 1 / Layer 2 / Layer 3 pilot types.
- **Scope estimate:** small

#### `docs/fundraising/PR-STRATEGY.md`, `docs/fundraising/VC-TARGET-LIST.md`, `docs/fundraising/PARTNER-TARGET-LIST.md`, `docs/fundraising/WARM-INTRO-REQUEST.md`, `docs/fundraising/ALTERNATIVE-FUNDING.md`
- **Layer:** multi
- **State:** ALIGNED
- **Proposed change:** Small per-file notes (cite POSITIONING.md / clarify layer in boilerplate language) — see Agent 5 detailed notes.
- **Scope estimate:** small each

#### `src/site/solutions-providers.html`
- **Layer:** n/a (stub)
- **State:** STALE-DEPRECATE
- **Proposed change:** Remove from navigation until content exists, or give it a concrete ETA.
- **Scope estimate:** small

#### `src/site/assessment.html`
- **Layer:** 1
- **State:** STALE-UPDATE
- **Proposed change:** Update v1.0 references to the current version, or deprecate and redirect to `release-notes.html`.
- **Scope estimate:** small

#### `src/site/cho-ar-marketing.html`, `src/site/cho-capitation-marketing.html`, `src/site/cho-ffs-payments-marketing.html`, `src/site/cho-premium-billing-marketing.html`
- **Layer:** 3
- **State:** MIS-LAYERED
- **Current framing:** Rich UX showcases of Layer 3 services without any note that these domains are part of the full platform under active hardening.
- **Proposed change:** Add a footer banner on each: "This is a demonstration interface for a CHO Layer 3 service; the underlying service is part of the full platform under active hardening ahead of pilot deployment. For Layer 2 adapter-based integration, see platform.html."
- **Scope estimate:** small each

#### `src/site/claims-repricing.html`, `src/site/pricing-api.html`
- **Layer:** product-specific
- **State:** ASSUMED-ALIGNED (spot-check only)
- **Proposed change:** Read in full during Phase 2 to confirm.
- **Scope estimate:** small

#### `src/site/docs/*.html` (22 files: api.html, architecture.html, benefit-plan-guide.html, claims-guide.html, commercial-licensing.html, compliance.html, deployment.html, eligibility-guide.html, fee-schedule-engine.html, fee-schedule-guide.html, finance-guide.html, florida-compliance.html, index.html, million-claim-challenge.html, prior-auth-guide.html, provider-enrollment-guide.html, provider-verification-guide.html, quickstart-kubernetes.html, quickstart.html, terminology-guide.html, texas-compliance.html, texas-tmppm-pa-rules.html)
- **Layer:** multi (developer / evaluator docs)
- **State:** ASSUMED-ALIGNED (audit spot-checked `index.html`, `claims-guide.html`, `prior-auth-guide.html`)
- **Proposed change:** Phase 2 should explicitly confirm each Layer-3 service guide has a "this is part of the full Layer 3 platform" note.
- **Scope estimate:** small per file

#### `src/site/README.md`, `src/site/IMPLEMENTATION-SUMMARY.md`, `src/site/BUILD-PROCESS.md`, `src/site/DEPLOYMENT.md`
- **Layer:** internal
- **State:** ALIGNED
- **Proposed change:** No change needed.
- **Scope estimate:** —

### P2 — internal / low-traffic

- `docs/deployment/DEPLOYMENT-STATUS-REPORT.md` — STALE-SNAPSHOT. An old "CRITICAL production failure" report left without a closure note; either add resolution or move to `docs/archive/incidents/`.
- `docs/deployment/DOCKER-BUILD-STATUS.md` — STALE-SNAPSHOT. Convert to an evergreen runbook or archive.
- Other `docs/deployment/*.md` (COSMOS-DB-DEPLOYMENT, DEPLOYMENT-CHECK-SUMMARY, DEPLOYMENT-GATES-GUIDE, DEPLOYMENT-PIPELINE-CHANGES, DEPLOYMENT-SECRETS-SETUP, DEPLOYMENT-WORKFLOW-REFERENCE, DEPLOYMENT-WORKFLOW-VALIDATION, ONBOARDING, ONBOARDING-ENHANCEMENTS, DEPLOYMENT.md) — ASSUMED-ALIGNED; operational runbooks with no positioning surface. Spot-check during Phase 2.

## STALE — DEPRECATE

### `docs/architecture/AUTHENTICATION-TESTING-GUIDE.md`
- **Why:** Describes legacy Azure AD B2C auth, which was superseded by multi-tenant Microsoft Entra ID (new tenants cannot provision B2C after May 2025).
- **Replacement:** New (or yet-to-be-written) Entra ID authentication guide under `docs/architecture/`.
- **Proposed action:** Move to `docs/archive/authentication-legacy/` with a banner linking to the Entra ID guide.

### `docs/architecture/AUTHENTICATION-VISUAL-GUIDE.md`
- **Why:** Same legacy B2C flow as above.
- **Replacement:** Same Entra ID guide.
- **Proposed action:** Move to `docs/archive/authentication-legacy/` with banner.

### `docs/releases/RELEASE_NOTES_v3.0.0.md`
- **Why:** Historical release notes that read as a forward-looking wishlist rather than a shipped summary.
- **Replacement:** `docs/releases/README.md` (once updated) + POSITIONING.md.
- **Proposed action:** Retitle in place to "v3.0.0 Release Notes — Historical Reference (December 2025)" with a banner pointing to the current release and POSITIONING.md. If Phase 2 chooses to remove rather than retitle, move to `docs/archive/releases/`.

### `docs/releases/v3.0.0-features-overview.md`
- **Why:** Claims not supported by POSITIONING.md (FHIR R5, AI-powered claim analytics, predictive revenue cycle intelligence, "150+ payer integrations") presented as complete.
- **Replacement:** Current positioning is `docs/POSITIONING.md`; current release notes are `docs/releases/v4.1.0-FHIR-API-PROMINENCE.md`.
- **Proposed action:** Move to `docs/archive/releases/` with a banner that says "Superseded; some claims in this file are not supported by current positioning — see POSITIONING.md."

## MISSING

The prompt suggested five missing artifacts; each is evaluated below with an accept / reject and a brief outline.

### `docs/adr/005-appeals-bespoke-domain-fhir-facade.md` — ACCEPT

- **Why it should exist:** The load-bearing architectural decision from PR #678 — that appeals is a bespoke domain service with its own state machine, audit trail, field encryption, and Kafka publisher, with FHIR as a façade through `IFhirAppealAdapter` rather than a persistence schema — is not captured in the ADR log. Future domain modernizations (capitation, claims) will need to cite this precedent.
- **Target layer:** 2
- **Proposed outline:**
  - Context: why the previous appeals implementation was insufficient
  - Decision: bespoke domain + FHIR façade via `IFhirAppealAdapter`
  - Consequences: precedent for future domain modernization
  - Alternatives considered: FHIR-as-storage, extend legacy appeals
  - References: PRs #677, #678, #680, #681
- **Scope estimate:** small

### `docs/adr/006-three-layer-positioning-model.md` — ACCEPT

- **Why it should exist:** This positioning is load-bearing for product and commercial decisions; it deserves an ADR entry alongside the architectural decisions.
- **Target layer:** not-layer-specific (governance)
- **Proposed outline:**
  - Context: CHO scope outgrew "CMS-0057-F compliance layer" framing
  - Decision: adopt Layer 1 / Layer 2 / Layer 3 engagement model
  - Consequences: positioning, pricing, roadmap all derive from POSITIONING.md
  - References: `docs/POSITIONING.md`, `docs/status/POSITIONING-AUDIT.md`
- **Scope estimate:** small

### `docs/adr/007-shared-contracts-project.md` — ACCEPT

- **Why it should exist:** `CloudHealthOffice.Appeals.Contracts` is now the template for cross-service DTO sharing; the decision should be recorded so that later services copy the pattern rather than re-invent.
- **Target layer:** not-layer-specific (cross-cutting)
- **Proposed outline:**
  - Context: cross-service DTOs were previously duplicated or Newtonsoft-serialized
  - Decision: per-domain `*.Contracts` shared projects under `src/services/shared/` with System.Text.Json as the canonical serializer
  - Consequences: Layer 2 domain rollouts adopt this pattern
  - References: `src/services/shared/CloudHealthOffice.Appeals.Contracts/`
- **Scope estimate:** small

### `docs/deployment/APPEALS-RUNBOOK.md` — ACCEPT

- **Why it should exist:** The appeals-service now has a production-shape set of operational dependencies (Cosmos DB index migration, Kafka consumer group for X12 275, secret rotation for the field-encryption key, Argo `x12-275-ingest.yaml` upstream dependency). There is no operational runbook that pulls these together.
- **Target layer:** 2
- **Proposed outline:**
  - Service topology (appeals-service, fhir-service façade, x12-275-ingest workflow)
  - Secrets (Key Vault names, rotation)
  - Index migration procedure
  - Kafka consumer group monitoring and lag alarms
  - Common incidents and recovery steps
  - Contact / oncall
- **Scope estimate:** medium

### Three-layer sales deck outline under `docs/sales-materials/` — ACCEPT, with qualifier

- **Why it should exist:** The current two pitch decks (`PITCH-DECK-CONTENT.md` and `pitch-deck-v4.md`) both conflate layers. A salesperson needs a single source that lets them flex Layer 1, Layer 2, or Layer 3 depending on the meeting.
- **Qualifier:** rather than adding a third deck, Phase 2 should consolidate the existing two into a layered deck where each layer has a mode that can be dropped for a given meeting.
- **Target layer:** multi
- **Proposed outline:**
  - Slide 1: The three-layer model in one slide (derived from POSITIONING.md §Summary)
  - Slide 2-4: Layer 1 (use when meeting is a compliance-urgency buyer)
  - Slide 5-7: Layer 2 (appeals proof point, Operating Mode, future domains)
  - Slide 8-10: Layer 3 (full CAPS, honest today-state, pilot program)
  - Slide 11-12: Commercial shape / next step — layer-specific
- **Scope estimate:** medium

## Cross-cutting patterns

### 1. Service-count drift
Three different numbers appear across artifacts:
- `README.md` — 29
- `docs/features/MICROSERVICES-IMPLEMENTATION-STATUS.md` — 22
- `src/site/release-notes.html` — 29 (body) / 22 (footer)
- POSITIONING.md — 36 (direct count of `src/services/`)
- `docs/architecture/ADJUDICATION-PIPELINE.md` — describes 9 engines, implicitly consistent
- `src/site/release-notes.html` footer — "9 engines" (consistent with POSITIONING.md)

Fix: POSITIONING.md is canonical; all artifacts updating in Phase 2 should reconcile to 36 services / 9 engines.

### 2. Logic Apps migration debt
Nine feature documents still reference Azure Logic Apps as current architecture, despite ADR-004 closing the migration. Many of them have a header note at line 1 ("Logic Apps deprecated per ADR-004") but the body still reads as if Logic Apps were live. Affected:
- `docs/features/CMS-0057-F-COMPLIANCE.md`
- `docs/features/COMMERCIALIZATION.md` (scattered references)
- `docs/features/FHIR-INTEGRATION.md`
- `docs/features/PATIENT-ACCESS-API.md`
- `docs/features/PRIOR-AUTHORIZATION-API.md`
- `docs/features/AUTHORIZATION-REQUEST.md`
- `docs/features/AUTHORIZATION-INQUIRY.md`
- `docs/features/PRIOR-AUTH-IMPLEMENTATION-SUMMARY.md`
- `docs/features/RELEASE_NOTES.md`

Fix: a dedicated cleanup pass in Phase 2, out of scope for this PR.

### 3. "CHO is a compliance layer" framing
Multiple high-traffic artifacts lead with Layer 1 framing and never surface Layer 2 / Layer 3. Affected:
- `src/site/index.html`
- `src/site/platform.html`
- `src/site/solutions-payers.html`
- `docs/sales-materials/MARKETING-LANDING-PAGE-COPY.md`
- `docs/sales-materials/demo-materials/demo-script.md`
- `docs/sales-materials/proposals/sales-proposal-template.md`
- `docs/sales-materials/outreach-campaigns/cold-call-scripts.md`
- `docs/sales-materials/outreach-campaigns/email-templates.md`
- `docs/sales-materials/SALES-EMAIL-TEMPLATES.md`
- `README.md`

Fix: introduce the three-layer model and the appeals proof point in each of these. The POSITIONING.md §Summary paragraph is the canonical insert.

### 4. Layer 3 "proven / production-ready" language without disclosure
Multiple artifacts claim production readiness or imply deployed customer scale without disclosing the Layer 3 honest-today state. Affected:
- `docs/sales-materials/PITCH-DECK-CONTENT.md`
- `docs/sales-materials/pitch-deck-v4.md`
- `docs/sales-materials/SALES-PRODUCT-OVERVIEW.md`
- `docs/fundraising/INVESTOR-ONE-PAGER.md`
- `docs/fundraising/INVESTOR-MEETING-SCRIPT.md`
- `docs/releases/v3.0.0-features-overview.md`
- `docs/releases/RELEASE_NOTES_v3.0.0.md`
- `docs/announcements/v3.0.0-announcement.md`
- `docs/features/V4-LAUNCH-ROADMAP.md`
- `src/site/login.html`

Fix: the POSITIONING.md §Layer 3 honest-today paragraph is the canonical disclosure. Every overclaim finding needs either the disclosure block inline or a scoped-to-Layer-1-or-Layer-2 rewording.

### 5. IFhirDataAdapter status ambiguity
FHIR-related documents ship "production ready" labels on 837/835/278 mappings while the adapter implementations are still the mock versions outside appeals. Affected: `FHIR-INTEGRATION.md`, `FHIR-IMPLEMENTATION-SUMMARY.md`, `CMS-0057-F-COMPLIANCE.md`, `PATIENT-ACCESS-API.md`.

Fix: standard "adapter status" table inserted into each FHIR doc (270/271 live, 275 live for appeals, 278 / 837 / 835 mock — hardening in flight).

### 6. Roadmap / release-notes date drift
`docs/roadmap/CHO-Roadmap-Readme.md` (dated March 2026), `docs/roadmap/CHO-ENHANCEMENT-*.md` (dated March 2026), `docs/releases/README.md` (dated November 2024 in one spot). Phases and milestones that shipped with the appeals sequence in April 2026 are still marked as planned work.

Fix: a small date/status refresh pass, combinable with the Logic Apps cleanup.

### 7. Operating Mode nomenclature unknown to sales
The term "Operating Mode" (Augment / Replace / Legacy) is the Layer 2 risk-mitigation pattern and appears nowhere in sales materials — neither in cold-call scripts, proposals, MSAs, nor demo scripts. Without it, sales has no vocabulary for "we run shadow for 30 days then flip to authoritative." Fix: add the definition to `MSA template` (Definitions) and to a new "Layer 2 talking points" insert in the proposal template / email templates.

## Recommended phase 2 structure

**Pick: (b) sequenced fix, with two parallel streams inside each phase.**

- **Wave 1 (P0, ~1-2 weeks):** fix every OVERCLAIMS finding before any investor or evaluator engagement. This is seven documents (INVESTOR-ONE-PAGER, INVESTOR-MEETING-SCRIPT, pitch-deck-v4 Slide 11, PITCH-DECK-CONTENT Slide 4, SALES-PRODUCT-OVERVIEW, v3.0.0-announcement, v3.0.0-features-overview) plus the solutions-payers and site-index underclaims. Stream it in two parallel PRs: one for public-facing site/sales, one for fundraising materials.

- **Wave 2 (P1, ~2-3 weeks):** the three-layer framing rollout — each of the ten "compliance layer" framing artifacts gets the POSITIONING.md §Summary insert, plus the Layer 2 / appeals language added to proposal template, demo script, cold-call scripts, email templates. Single PR, reviewed by sales lead.

- **Wave 3 (P1/P2, ~1-2 weeks):** STALE-UPDATE cleanup in `docs/features/` and `docs/releases/`. This is the Logic-Apps-mention scrub, date refreshes, service-count reconciliations, and the `v3.0.0-features-overview.md` deprecation. Bundle as a single "documentation freshness" PR; it doesn't need sales review.

- **Wave 4 (parallel to Wave 3, ~1 week):** MISSING artifacts. ADR-005, ADR-006, ADR-007, APPEALS-RUNBOOK, three-layer sales deck outline. Separate small PRs per file; reviewers are whichever engineer or BD lead has context on each.

**Rough effort estimate:** 2-3 engineer weeks for the technical docs (Waves 3 + 4), 1-2 weeks for sales / marketing material (Waves 1 + 2 with sales lead review). Realistic calendar: ~5 weeks to close the whole audit.
