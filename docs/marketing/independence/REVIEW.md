# Health Plan Independence implementation review

Reviewed October 19, 2026 (America/Phoenix). Implementation branch: `feat/health-plan-independence`. Base includes merged PR #1410 and the subsequent `01164a94` metrics refresh. No merge or deployment is authorized by this PR.

## Implementation

- Replaced the homepage with the executive independence narrative, visible BSL disclosure, four control dimensions, responsible AI, incremental modernization, inspectable evidence, economics and partnership invitation.
- Added `/health-plan-independence`, `/economics-of-vendor-lock-in`, `/ai-ready-health-plan-architecture`, `/incremental-modernization`, `/founding-transformation-partners`.
- Added scoped CSS, native disclosure navigation, keyboard/mobile behavior, print styles and reduced-motion overrides. The assistant moves into document flow after the footer on mobile so it does not obscure copy or CTAs. No frontend framework or runtime dependency was added.
- Retained shared build-injected analytics, lead capture and assistant. Explicit CTA events use the existing precedence rule, avoiding automatic-event duplication. Added named page views, manifesto scroll engagement, calculator engagement/calculation and partnership form interests. Form success/error events reuse existing shared instrumentation.
- Removed free-text role and provider error text from analytics payloads, keeping submitted contact details in the lead record rather than GA4. Financial scenario values are neither persisted nor transmitted by the calculator.
- Updated the message sheet, assistant knowledge and corresponding runtime answers. Existing top-level acquisition pages gain a selective independence link; generated articles, technical docs, portal and authentication navigation remain untouched.

## SEO preservation

No Search Console exports were available. The homepage title conservatively retains CMS-0057-F and FHIR intent: **Health Plan Administration & CMS-0057-F FHIR APIs, On Your Terms | CloudHealthOffice**. The description still names CMS-0057-F/FHIR and coexistence with QNXT, Facets and HealthEdge. Existing CMS and technical destinations remain linked.

The artifact tests compare against `origin/main`: all **99 baseline HTML files** remain. Thirteen meaningful legacy content fragments now land on substantive CMS/FHIR, platform progression, engagement, services and next-step content with matching onward links. Tests verify their subject, parent section, visibility and scroll destination, rather than mere ID existence. Obsolete `fp-banner`, `fp-banner-dismiss` and `stickyCta` control IDs are retired; they were not content destinations. The redirect file is byte-for-byte unchanged, with no catch-all rules or newly introduced loops. Clean routes use the existing hosting convention; no infrastructure or historical Azure routing configuration was changed.

The existing sitemap generator runs in strict mode with full Git history. Its output has **95 unique URLs**, compared with 90 baseline entries: exactly the five new routes appear once. All original sitemap entries remain; error, login, portal and marketing mockup exclusions remain excluded. The generated `dist/sitemap.xml` is the deployment artifact; the curated source sitemap remains unchanged so unrelated priorities and dates are not rewritten.

All six executive pages have one H1, a unique title/description, HTTPS canonical, matching social URL and parseable WebPage JSON-LD. Local href/src targets and fragment links resolve in built output. Existing remote documentation/source links were checked against repository paths, not represented as a complete remote-link availability crawl. Local clean-route testing reproduces file resolution. The read-only hosted checker additionally verifies clean routes, canonical redirects and a calculator asset hash against an existing hosted preview; see the final-head evidence procedure below. Production-domain release validation remains separate.

## Capability claim review

The governing source is [CAPABILITY_RECONCILIATION.md](../../CAPABILITY_RECONCILIATION.md), including its dated tests and scope. Application tests were not rerun as part of this marketing change.

| Significant claim | Evidence | Qualification in the experience |
| --- | --- | --- |
| Source available; commercial production license required | `LICENSE`, `COMMERCIAL-LICENSING.md` | Visible hero/footer disclosure; manifesto explains obligations and exit terms. No ownership grant or zero-price licensing assumption. |
| Customer-controlled infrastructure | Existing `/deploy`, deployment documentation | Responsibilities and deployment-specific validation required; no universal portability or data-boundary guarantee. |
| Incremental modernization | Existing `/platform#layers`, deployment material | Foothold → Domain Cutover → Core Modernization; reconciliation, acceptance, ownership and rollback required. No disruption-free promise. |
| Advisory AI implementation | `src/ai/edi277Resolution.ts`; `src/services/claims-examiner-service/Services/Examiner/ExaminerOrchestrator.cs` | Scoped advisory implementation, not validated autonomous adjudication or clinical decisions. Existing supporting tests inspected: `src/ai/__tests__/edi277Resolution.test.ts` (mock suggestions/redaction) and `tests/CloudHealthOffice.ClaimsExaminerService.Tests/ExaminerOrchestratorTests.cs` (scope filter, parsed tool result and human-escalation fallback); not rerun. Model credentials, data-flow review and evaluation required. |
| PAS persistence and line outcomes | CAP-01, CAP-02 | Stored request/decision and service-line behavior, not full IG conformance. |
| CRD uncertainty handling | CAP-03 | Unknown inputs/failure do not become no-authorization-needed assertions. |
| Durable DTR creation/read-back | CAP-04 | Updates/search indexes remain in-process; limited package/prepopulation preserved (CAP-15). |
| SMART context and approval withdrawal | CAP-05 | No assertion of every launch field or revocation mechanism. |
| CMS metrics and receipt deadlines | CAP-06, CAP-07 | Named implemented scope; complete reporting and deployment policy remain separate. |
| Authorization concurrency | CAP-12, CAP-21 | Current Mongo repository scope; not distributed transactions or universal backend concurrency. |
| Specific limits | CAP-14, CAP-15, CAP-18, CAP-19, CAP-22 | Incomplete export, DTR package/prepopulation, placeholder fraud model, mock default adapters, unverified conformance/adoption/outcomes remain visible. |
| Synthetic evidence | Existing `/evidence` and MCC archive | Supports evaluation, not production outcomes or customer adoption. |
| Economics | Explicit buyer inputs; calculator arithmetic tests | No incumbent pricing claim, quote or guaranteed savings; unknown licensing/support blocks totals. |

## Follow-up fixes and evidence identity

The approved Health Plan Independence narrative, licensing qualifications and PR #1410 capability classifications are unchanged. Source/test revision **`dec71610ba665ef3ed7df69c0035a7b8ecc76bf0`** was validated from a clean checkout against explicit baseline **`01164a944c7c7a36bdb46cf0cfbab08d9e87e847`**. [source-validation.json](source-validation.json) records that clean source head, baseline, checks and SHA-256 hashes for the twelve responsive screenshots plus printable-results screenshot. The [Linux source validation run](https://github.com/aurelianware/cloudhealthoffice/actions/runs/39338617055) also passed at this SHA. Its first predecessor caught a Linux-font mobile overflow; the committed heading/button sizing fix passed the rerun. Subsequent review/evidence commits change documentation and images only.

A report cannot embed the SHA of the commit that contains itself. Final-head evidence is therefore generated **after** committing: the PR description identifies the exact final SHA and links its [Marketing independence validation run](https://github.com/aurelianware/cloudhealthoffice/actions/workflows/marketing-independence.yml). That run uploads `independence-<full-head-SHA>` with a fresh `evidence.json` and screenshots. The workflow explicitly checks out the PR head, fetches its base SHA into `refs/remotes/origin/marketing-baseline`, and requires complete Git history. This avoids presenting a test of a merge commit, stale main ref or previous code revision as final-head validation. Local final-head screenshots are compared with the tracked PNG hashes; Linux CI captures are separately identified and may rasterize fonts differently.

### Calculator fallback privacy

The calculator has **no native form**, no named/form-associated financial controls and no submit buttons. Its outer fieldset and print button are disabled in source HTML, with an explicit unavailable message. Only complete successful initialization enables controls and announces readiness. The button and Enter use explicit local calculations. If JavaScript is disabled, blocked, throws during initialization or its handlers later stop working, there is no native request/URL serialization path for financial inputs. Native input validity is still checked before calculation.

### Legacy destinations and keyboard entry

`cms-explainer` / `audience-heading` target CMS/FHIR resources; `platform-flow`, `claims-pipeline-heading` and `how-it-works-heading` target the modernization progression; `engage` / `engage-heading` target the partnership invitation; `software-services` / `software-services-heading` target services; `advisory`, `advisory-heading`, `next-step` and `next-step-heading` target deployment discussion. All six executive main landmarks now accept skip-link focus.

## Validation

- `SITEMAP_STRICT=1 npm run build:site`: passed with full history, publication generation and sitemap. **95 unique sitemap URLs**, all baseline entries retained; no redirect-file changes.
- `npm --prefix src/site run test:independence`: **13 passed**, including seven arithmetic tests and six artifact, route, metadata, fallback, meaningful-fragment and contrast checks. `CHO_BASELINE_REF` explicitly selects the fetched comparison commit.
- Existing marketing positioning suite: **146 passed**.
- Browser checks passed at **1440px and 390px** for all six pages: no overflow, one H1, matching canonical/social URL, keyboard skip focus on every page, home menu/disclosure/Escape focus, and thirteen content fragment destinations.
- Privacy checks cover disabled JavaScript, a blocked calculator script and an injected formatter initialization failure. Unavailable/disabled state is asserted; button and Enter attempts produce no requests, including after test-only forced re-enabling of unnamed controls.
- Valid, unique sentinel costs respect each field's step/min constraints. Both button and Enter produce successful recalculations; six exact computed outputs and incrementing success-event counts are asserted. The actual `analytics-events.js`, `choTrack` and build-injected `gtag` queue run unchanged. Every observed request URL, body and header, analytics event payload and browser storage is inspected for sentinels. **The remote GTM/GA loader is blocked; live GA receipt is not claimed.**
- Both shared Formspree pathways retain mocked failure → retry → accepted response checks. The actual analytics queue records an error on failure and success only after acceptance; focus and PII exclusion are checked. External form requests are intercepted; **zero real inquiries or calendar bookings** are sent. Endpoint acceptance is not recipient/delivery verification.
- Printable results include the successfully recalculated amounts and assumptions; the assistant and print action are hidden in print media. [Printable-results capture](screenshots/calculator-print.png) uses synthetic sentinel values, not real financial information.
- `npm run validate:site` completes with no findings on the six executive pages and **21 potential pre-existing-page issues**. Contrast/keyboard checks are bounded, not a complete WCAG or assistive-technology audit.
- New `.github/workflows/marketing-independence.yml` runs the strict build, independence unit/artifact checks, installed Chromium browser checks and head-labelled evidence upload on PRs, including evidence/report changes. It has read-only repository permissions and no publish/deploy step. Dependency versions/lockfiles and existing deployment workflows are unchanged.

### Hosted preview verification

The existing PR preview **[pr-1411.cloudhealthoffice.pages.dev](https://pr-1411.cloudhealthoffice.pages.dev)** is publicly accessible (no access gate was encountered). [Recorded hosted source checks](source-hosted-validation.json) passed at 2026-10-19 23:00:12 UTC, after [preview run 39338617045](https://github.com/aurelianware/cloudhealthoffice/actions/runs/39338617045) completed successfully for source SHA `dec71610ba665ef3ed7df69c0035a7b8ecc76bf0`: all six clean routes returned 200; HTML/slash alternatives returned 308 to clean paths, `/docs/` returned 301 to `/docs`, final destinations resolved without loops, apex production canonicals were retained, and the calculator JS matched local SHA-256 `9f302df27f1d88726589111275658449aaed994b46d76d892e77a29a9137e2c1`. Clean preview responses carry `X-Robots-Tag: noindex`. This source-level record is distinct from the final-head recheck linked in the PR description. Final verification uses `CHO_HOSTED_PREVIEW=<existing-preview> node src/site/tests/hosted-independence.cjs` after the repository's existing PR preview workflow refreshes. The checker records the local head SHA, UTC time, each status/Location/canonical check and the deployed calculator JS hash, rejects stale assets, and records failures/access limitations instead of claiming success. The PR description links the final preview workflow's exact `headSha` and hosted-check results. No manual deployment is performed.

Reproduce local browser checks with `CHO_EVIDENCE_DIR=/tmp/independence-evidence node src/site/tests/independence-browser.cjs` after building. It starts its own local clean-route fixture; `CHO_CHROME_PATH` can select installed Chrome. Hosted checks only issue GET requests and require an existing HTTPS preview; they never create or publish one.

## Review screenshots

| Page | Desktop | Mobile |
| --- | --- | --- |
| Homepage | [Desktop](screenshots/index-desktop.png) | [Mobile](screenshots/index-mobile.png) |
| Manifesto | [Desktop](screenshots/health-plan-independence-desktop.png) | [Mobile](screenshots/health-plan-independence-mobile.png) |
| Economics calculator | [Desktop](screenshots/economics-of-vendor-lock-in-desktop.png) | [Mobile](screenshots/economics-of-vendor-lock-in-mobile.png) |
| AI architecture | [Desktop](screenshots/ai-ready-health-plan-architecture-desktop.png) | [Mobile](screenshots/ai-ready-health-plan-architecture-mobile.png) |
| Incremental modernization | [Desktop](screenshots/incremental-modernization-desktop.png) | [Mobile](screenshots/incremental-modernization-mobile.png) |
| Partnership | [Desktop](screenshots/founding-transformation-partners-desktop.png) | [Mobile](screenshots/founding-transformation-partners-mobile.png) |

## Outstanding owner decisions before launch

Confirm partnership scope, commercial terms and legal approval of licensing representations. Verify Formspree recipient ownership and delivery, spam controls and lead follow-up responsibilities. Confirm the production hosting/release owner before any production deployment; hosted preview evidence does not authorize release. Review existing dependency advisories and accessibility issues separately. Full standards conformance, payer-specific model quality, customer deployments, operational performance and financial outcomes remain unverified; this PR makes no stronger claims.

The local repositioning plan found in the prior `cho-phase6` checkout informed the design; current merged capability evidence takes precedence over its historical gap labels. No application services, API contracts, authentication, infrastructure, existing deployment workflows, licenses or companion-site repository were modified. The new workflow is validation-only.
