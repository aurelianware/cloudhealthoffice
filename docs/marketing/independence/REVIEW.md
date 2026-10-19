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

The artifact tests compare against `origin/main`: all **99 baseline HTML files** remain and every existing homepage `id` is retained. Existing fragment IDs are compatibility destinations; obsolete promotional controls are not restored. The redirect file is byte-for-byte unchanged, with no catch-all rules or newly introduced loops. Clean routes use the existing hosting convention; no infrastructure or historical Azure routing configuration was changed.

The existing sitemap generator runs in strict mode with full Git history. Its output has **95 unique URLs**, compared with 90 baseline entries: exactly the five new routes appear once. All original sitemap entries remain; error, login, portal and marketing mockup exclusions remain excluded. The generated `dist/sitemap.xml` is the deployment artifact; the curated source sitemap remains unchanged so unrelated priorities and dates are not rewritten.

All six executive pages have one H1, a unique title/description, HTTPS canonical, matching social URL and parseable WebPage JSON-LD. Local href/src targets and fragment links resolve in built output. Existing remote documentation/source links were checked against repository paths, not represented as a complete remote-link availability crawl. Local clean-route testing reproduces file resolution; production CDN behavior must be checked in the hosting preview.

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

## Validation

- Locked site dependencies installed with `npm ci --prefix src/site --ignore-scripts`. Locked root dependencies installed with lifecycle scripts disabled to run the existing tests; package/lockfiles were not upgraded. Root installation reported 20 moderate advisories in existing dependencies; dependency remediation is outside the authorized marketing scope.
- `SITEMAP_STRICT=1 npm run build:site` passed with the existing publication generator, HTML build and sitemap generation.
- `npm run validate:site` completes; it reports no issues on the six executive pages and **21 potential issues on existing pages**. This legacy validator is not a complete accessibility audit.
- Existing marketing positioning suite: **146 tests passed**.
- New `npm --prefix src/site run test:independence`: **11 tests passed**, including seven arithmetic/invalid/unknown/edge-case calculator tests and four artifact/route/metadata/contrast checks. Run after building.
- `node src/site/tests/independence-browser.cjs` (starts its own local clean-URL fixture unless `CHO_PREVIEW_URL` is set; `CHO_CHROME_PATH` can select installed Chrome) passed: six routes at 1440px and 390px, no horizontal overflow, one H1/canonical, visible skip-link focus, mobile menu/Escape/focus return, reduced-motion rendering, calculator stale-result invalidation and non-transmission, and both inquiry forms' failure/retry/acceptance/focus behavior.
- Browser requests to external services were blocked. Formspree POST responses were mocked (500 then 200); successful-submit events occurred only after mocked acceptance. No real inquiry, email or calendar booking was sent. The built forms resolve to the existing configured endpoint `https://formspree.io/f/xgojygon`, but this does **not** establish inbox delivery or recipient ownership.
- Contrast checks use the actual executive foreground/background tokens, including errors, accent links, button text and focus color. Keyboard and responsive checks are bounded, not a full assistive-technology or WCAG audit.
- Generated `dist` was inspected and checked; test sources are excluded from the public artifact. All screenshots below come from that local built output. Calculator screenshots load the explicitly hypothetical example rather than implying a commercial quote.

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

Confirm partnership scope, commercial terms and legal approval of licensing representations. Verify Formspree recipient ownership and delivery, spam controls and lead follow-up responsibilities. Verify the production host and clean-URL preview behavior. Review existing dependency advisories and accessibility issues separately. Full standards conformance, payer-specific model quality, customer deployments, operational performance and financial outcomes remain unverified; this PR makes no stronger claims.

The local repositioning plan found in the prior `cho-phase6` checkout informed the design; current merged capability evidence takes precedence over its historical gap labels. No application services, API contracts, authentication, infrastructure, deployment workflows, licenses or companion-site repository were modified.
