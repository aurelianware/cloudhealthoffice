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

## Unresolved / Manual Review Needed

1. **`docs/fundraising/PR-STRATEGY.md` lines 162, 234** — Podcast pitch and speaker bio describe CHO as "first open-source solution" and "first open-source platform." These are borderline — they could be kept as historical positioning or updated. Flagged for manual decision.

2. **`docs/fundraising/INVESTOR-MEETING-SCRIPT.md` line 302** — "Why open source?" Q&A section. The question heading was left as-is for continuity but the answer should reference BSL-1.1 model.

3. **`docs/sales-materials/MARKETING-LANDING-PAGE-COPY.md` line 276** — FAQ question "What does 'open source' mean for a healthcare platform?" Left as-is for SEO value. Consider updating the answer to clarify BSL-1.1 vs OSI-approved open source.

## Recommendations

1. Add a `COMMERCIAL-LICENSING.md` to the repo with clear guidance on what's free vs paid
2. Add a licensing CTA to the website (tasteful, non-intrusive)
3. Add a subtle licensing notice in the portal footer
4. Consider a pre-commit hook or CI check that flags new "open-source" references describing CHO
