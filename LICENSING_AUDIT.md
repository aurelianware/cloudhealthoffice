# Licensing Audit — Cloud Health Office

**Audit Date:** 2026-03-27
**Auditor:** Automated scan + manual review
**License:** Business Source License 1.1 (BSL-1.1)

---

## Summary

Cloud Health Office is licensed under BSL-1.1 (source-available, not open-source).
This audit identified and corrected 50+ instances across 30+ files where the project
was incorrectly described as "open-source" or "open source."

## Authoritative License References

| File | Status |
|------|--------|
| `LICENSE` | Correct — BSL-1.1, converts to Apache 2.0 on 2030-03-08 |
| `LICENSE_SUMMARY.md` | Correct — clearly states BSL-1.1, non-production free |
| `README.md` (line ~303) | Correct — explicitly says "source-available" and "not open source software" |
| `package.json` | Correct — `"license": "BUSL-1.1"` |

## Issues Found and Resolved

### Category 1: Core Documentation (9 files)
- `CONTRIBUTING.md` — "open-source" → "source-available"
- `.github/copilot-instructions.md` — "#1 open-source" → "#1 source-available"
- `docs/governance/GOVERNANCE.md` — "open-source" → "source-available"
- `docs/deployment/ONBOARDING.md` — "open-source" → "source-available"
- `docs/security/WHITEPAPER-CMS-0057-F-COMPLIANCE.md` — 3 instances fixed
- `CHANGELOG.md` — 2 instances fixed
- `docs/guides/QUICKSTART.md` — "#1 open-source" → "#1 source-available"
- `docs/architecture/BRANDING-IMPLEMENTATION-SUMMARY.md` — 2 instances fixed
- `docs/deployment/DOCKER-BUILD-STATUS.md` — 1 instance fixed

### Category 2: Sales Materials (7 files, ~19 instances)
- `SALES-PRODUCT-OVERVIEW.md`
- `PITCH-DECK-CONTENT.md`
- `SALES-EMAIL-TEMPLATES.md`
- `MARKETING-LANDING-PAGE-COPY.md`
- `pitch-deck-v4.md`
- `SALES-CASE-STUDY-TEMPLATE.md`
- `FINANCIAL-MODEL.md`

### Category 3: Fundraising (8 files, ~20 instances)
- `INVESTOR-ONE-PAGER.md`
- `INVESTOR-MEETING-SCRIPT.md`
- `README.md` (fundraising)
- `WARM-INTRO-REQUEST.md`
- `PARTNER-TARGET-LIST.md`
- `VC-TARGET-LIST.md`
- `PILOT-TO-FUNDING.md`
- `DUE-DILIGENCE-CHECKLIST.md`

### Category 4: Website (5 files)
- `src/site/platform.html` — "first open-source" → "first source-available"
- `src/site/index.html` — JSON-LD description updated
- `src/site/solutions-payers.html` — "Open-source core" → "Source-available core (BSL 1.1)"
- `src/site/cms-0057f-compliance.html` — footer updated
- `src/site/portal/api-docs.html` — "Open Source" badge → "Source-Available (BSL 1.1)"

### Category 5: Other Docs (4 files)
- `api/quickstarts/cms-0057f-compliance-quickstart.md` — cost claim clarified
- `api/quickstarts/patient-access-quickstart.md` — cost claim clarified
- `docs/features/V4-LAUNCH-ROADMAP.md` — 1 instance fixed
- `docs/features/RELEASE_NOTES.md` — 1 instance fixed
- `docs/features/ROADMAP-2026.md` — 1 instance fixed
- `docs/features/SAAS-LAUNCH-READINESS.md` — 2 instances fixed
- `docs/announcements/v3.0.0-announcement.md` — 2 instances fixed

## Intentionally Unchanged

| File | Reason |
|------|--------|
| `CONTRIBUTING.md` lines 545-564 | DCO (Developer Certificate of Origin) standard boilerplate — uses "open source license" as legal term of art |
| `docs/images/README.md` line 47 | Refers to GIMP (third-party tool), not CHO |
| `docs/fundraising/VC-TARGET-LIST.md` line 366 | Describes Lightspeed's investment focus, not CHO |
| `docs/fundraising/PR-STRATEGY.md` lines 124, 126, 166 | General industry references ("open source for healthcare", "open source communities") |
| `docs/features/ROADMAP.md` line 122 | Refers to "open-source components" (third-party K8s tooling) |
| `docs/features/WHATS-NEW.md` line 19 | Refers to HashiCorp Vault (third-party) |
| `src/site/insights.html` lines 15, 291, 428 | Market analysis commentary about industry trends |
| `src/site/legal/privacy-policy.html` footer | Fixed |
| `src/site/pricing-api.html` | Refers to API free tier pricing, not BSL license |

## Previously Unresolved — Now Resolved (2026-03-27, Pass 2)

1. **`docs/fundraising/PR-STRATEGY.md`** — "Pillar 3: Open Source in Healthcare" updated to
   "Source-Available Software in Healthcare." Line 166 "open source" → "source-available."
2. **`docs/sales-materials/MARKETING-LANDING-PAGE-COPY.md` line 276** — FAQ question updated from
   "What does 'open source' mean" to "What does 'source-available' mean." Answer updated to
   explain BSL 1.1 and clarify non-production vs. production licensing.
3. **`docs/security/WHITEPAPER-CMS-0057-F-COMPLIANCE.md` line 831** — Footer tagline updated from
   "Open Source" to "Source-Available (BSL 1.1)."

## Pass 2 — Additional Issues Found and Resolved (2026-03-27)

### Public Pricing Removal

Pricing was publicly listed in multiple files. All specific dollar amounts for Cloud Health
Office subscriptions have been replaced with "Contact us" / "[Per Agreement]" messaging.
Competitor/market pricing (public data) retained for context where appropriate.

| File | Change |
|------|--------|
| `README.md` | PMPM pricing table removed; replaced with "Contact us" CTA |
| `COMMERCIAL-LICENSING.md` | Tier prices removed; "Contact us" messaging |
| `docs/archive/marketplace-logic-app-era/PRICING.md` | Archived header added; content marked deprecated |
| `docs/archive/marketplace-logic-app-era/legal/terms-of-service.md` | Subscription/overage prices → [Per Agreement] |
| `docs/sales-materials/SALES-ROI-CALCULATOR.md` | CHO prices removed; competitor data retained; "Contact us" CTA |
| `docs/sales-materials/contracts/order-form-template.md` | All dollar amounts → [Per Agreement] |
| `docs/sales-materials/MARKETING-LANDING-PAGE-COPY.md` | FAQ 4 price reference removed |
| `src/services/CloudHealthOffice.PricingApi/README.md` | Paid tier prices → "Contact us" |
| `.github/ISSUE_TEMPLATE/v4-legal-finalization.md` | Pricing table, Stripe config, email template prices removed |
| `src/site/pricing.html` | All tier prices → "Contact Us"; monthly/annual toggle removed |
| `src/site/pricing-api.html` | Paid tier prices → "Contact Us" |
| `src/site/cms-0057f-compliance.html` | Tier prices → "Contact Us" |
| `src/site/release-notes.html` | Tier prices and PMPM rates → "Contact us" |
| `src/site/docs/deployment.html` | Tier prices removed from code comments |
| `docs/sales-materials/pitch-deck-v4.md` | All tier prices, beta prices, overage rates removed |
| `docs/sales-materials/SALES-PRODUCT-OVERVIEW.md` | Tier prices removed |
| `docs/sales-materials/FINANCIAL-MODEL.md` | Tier prices removed |
| `docs/sales-materials/PILOT-PROGRAM.md` | Tier prices and discount amounts removed |
| `docs/sales-materials/SALES-EMAIL-TEMPLATES.md` | Tier prices removed |
| `docs/sales-materials/PITCH-DECK-CONTENT.md` | Tier prices removed |
| `docs/guides/QUICKSTART.md` | Tier prices removed |
| `docs/guides/FEATURES.md` | Tier prices removed |
| `docs/guides/DEPLOYMENT.md` | Tier prices removed from comments |
| `docs/deployment/DEPLOYMENT.md` | Tier prices removed |
| `docs/deployment/ONBOARDING.md` | Tier prices removed |
| `docs/features/ROADMAP-2026.md` | Tier prices removed |
| `docs/features/ROADMAP.md` | Tier prices removed |
| `docs/features/RELEASE_NOTES.md` | Tier prices removed |
| `docs/features/WEBSITE-PHASE2-COMPLETE.md` | Tier prices removed |
| `docs/features/WHATS-NEW.md` | Tier prices removed |
| `docs/releases/RELEASE_NOTES_v3.0.0.md` | Tier prices removed |
| `CHANGELOG.md` | Tier prices removed |
| `.github/ISSUE_TEMPLATE/v4-clearinghouse-integration.md` | Tier prices removed |
| `docs/security/STRATEGIC_PLAN_CLOUD_HEALTH_OFFICE.md` | Pricing references removed |
| `docs/archive/marketplace-logic-app-era/original-README.md` | Tier prices removed |
| `scripts/setup/setup-stripe.sh` | Price display strings removed from echo statements |
| `src/portal/.../Pricing.razor` | PMPM prices and "Open Source" tier name updated |
| `src/portal/.../Signup.razor` | Tier price display → "Contact sales" |
| `src/portal/.../Settings.razor` | Tier price display → "Contact sales" |
| `src/portal/.../AddEditTenantDialog.razor` | Price labels removed from tier dropdown |
| `src/portal/.../Welcome.razor` | Price range removed from pricing link |

### Wording Fixes ("open source" → "source-available")

| File | Change |
|------|--------|
| `docs/archive/marketplace-logic-app-era/legal/privacy-policy.md` | Footer tagline |
| `docs/archive/marketplace-logic-app-era/legal/support-terms.md` | Footer tagline |
| `docs/archive/marketplace-logic-app-era/legal/terms-of-service.md` | Footer tagline |
| `docs/archive/marketplace-logic-app-era/legal/sla.md` | Footer tagline |
| `docs/archive/marketplace-logic-app-era/original-README.md` | Footer tagline |
| `docs/archive/marketplace-logic-app-era/managed-app/createUiDefinition.json` | "#1 open-source" → "#1 source-available" |
| `docs/sales-materials/SALES-ROI-CALCULATOR.md` | "Open Source" → "Source-Available" in benefits table |
| `docs/sales-materials/SALES-PRODUCT-OVERVIEW.md` | CHO "open source" → "source-available" |
| `docs/sales-materials/MARKETING-LANDING-PAGE-COPY.md` | "open source" → "source-available" in value props and badges |
| `docs/sales-materials/pitch-deck-v4.md` | CHO "open source" → "source-available" |
| `docs/sales-materials/PITCH-DECK-CONTENT.md` | CHO "open source" → "source-available" |
| `docs/security/WHITEPAPER-CMS-0057-F-COMPLIANCE.md` | "Open Source Alternative" → "Source-Available Alternative" |
| `docs/security/THIRD-PARTY-AUDIT-PROCESS.md` | "open-source" → "source-available" |
| `docs/fundraising/INVESTOR-MEETING-SCRIPT.md` | CHO "open source" → "source-available" |
| `docs/fundraising/DUE-DILIGENCE-CHECKLIST.md` | "open source" → "source-available" |
| `docs/fundraising/ALTERNATIVE-FUNDING.md` | "open source" → "source-available" |
| `docs/features/ROADMAP-2026.md` | 3 CHO "open source" instances → "source-available" |
| `docs/features/CMS-0057-F-COMPLIANCE.md` | "open source" → "source-available" |
| `docs/features/WHATS-NEW.md` | "open source" → "source-available" |
| `docs/guides/QUICKSTART.md` | "open source" → "source-available" |
| `docs/guides/FEATURES.md` | "open source" → "source-available" |
| `CHANGELOG.md` | "First in Open Source" → "First in Source-Available" |
| `scripts/cli/interactive-wizard.ts` | "#1 open-source" → "#1 source-available" |
| `src/portal/.../Legal.razor` | "Open Source" heading → "Source-Available (BSL 1.1)" |
| `src/portal/.../Pricing.razor` | "Open Source" tier name, FAQ text updated |
| `src/portal/.../Welcome.razor` | "#1 Open-Source" → "#1 Source-Available" |

## Intentionally Unchanged (Confirmed)

| File | Reason |
|------|--------|
| `CONTRIBUTING.md` lines 545-564 | DCO standard boilerplate — "open source license" is a legal term of art |
| `docs/images/README.md` line 47 | Refers to GIMP (third-party tool), not CHO |
| `docs/fundraising/VC-TARGET-LIST.md` line 366 | Describes Lightspeed's investment focus, not CHO |
| `docs/features/ROADMAP.md` line 122 | Refers to third-party K8s tooling |
| `docs/features/WHATS-NEW.md` line 19 | Refers to HashiCorp Vault (third-party) |
| `src/site/insights.html` lines 15, 291, 428 | Market analysis commentary about industry trends |
| `src/site/pricing-api.html` free tier ($0) | Free tier for API is a product feature, not BSL licensing |
| `src/site/index.html` "$2M+ upgrades" | Refers to competitor/market costs, not CHO pricing |
| `src/site/solutions-payers.html` cost comparisons | Refers to competitor/market costs, not CHO pricing |
| `docs/fundraising/INVESTOR-MEETING-SCRIPT.md` line 302 | Already uses "source-available" correctly |
| `docs/fundraising/INVESTOR-MEETING-SCRIPT.md` line 148 | Industry commentary about open source credibility |
| `docs/sales-materials/pitch-deck-v4.md` line 655 | Industry trend ("Open Source Momentum") |
| `docs/sales-materials/PITCH-DECK-CONTENT.md` line 112 | Industry trend ("Open source momentum") |
| `scripts/setup/setup-stripe.sh` `--unit-amount` values | Functional Stripe API parameters; update via config, not source removal |

## Remaining Manual Review

| Item | Notes |
|------|-------|
| `scripts/setup/setup-stripe.sh` hardcoded `--unit-amount` values (49900, 149900 cents) | These are Stripe API call parameters. Consider externalizing to env vars or config file. |
| `docs/archive/marketplace-logic-app-era/PRICING.md` body content | Archived with deprecation header; full content still present (strikethrough). Consider deletion if archive policy permits. |

## Recommendations

1. ~~Add a `COMMERCIAL-LICENSING.md` to the repo~~ — Done (exists)
2. Add a licensing CTA to the website (tasteful, non-intrusive)
3. Add a subtle licensing notice in the portal footer
4. Consider a pre-commit hook or CI check that flags new "open-source" references describing CHO
5. Keep pricing in a private/internal-only document not committed to the public repo
6. Externalize Stripe `--unit-amount` values in `setup-stripe.sh` to environment variables
