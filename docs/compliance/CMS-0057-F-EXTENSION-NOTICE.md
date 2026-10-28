# CMS-0057-F Decision-Timeframe Extension Notice

**Status as of:** October 2026

When a Medicare Advantage or Medicaid prior-authorization decision timeframe is
extended (up to 14 calendar days, once), the member must get a written notice
that gives the reason for the delay and their right to file a grievance if they
disagree. For Medicare Advantage that is an expedited grievance. Cloud Health
Office records the extension and the notice on the authorization. The plan
authors and approves the letter itself.

This is not legal advice. Confirm notice content, timing and language
requirements with compliance counsel and the state contract.

## How it works

1. A reviewer with `authorizations:decide` opens the authorization record's
   **Review** tab. On an open Medicare Advantage or Medicaid request, the
   **Decision timeframe** panel offers the extension. The reviewer picks a
   ground (member request, provider request, plan needs additional
   information) and writes the reason that will go to the member.
   This calls `POST api/authorizations/{id}/decision-extension`.
2. The panel then shows the notice as owed and offers **Send extension
   notice**. That button:
   - reads the member's mailing address from the member record and refuses to
     continue if it is incomplete;
   - requests a letter of type `PA-DECISION-EXTENSION` from
     correspondence-service (`POST api/v1/correspondence/letters`), with the
     authorization and the member as subjects;
   - records the letter id on the extension
     (`POST api/authorizations/{id}/decision-extension/member-notice`), which
     is idempotent for the same letter.
3. Requests that were extended without a recorded notice are counted as
   `extensionNoticesOwed` in the CMS-0057-F metrics. Each one is flagged as
   `extensionNoticeOwed` in the recent-decision list.

## Plan setup

Create the letter type and an approved template for each language, format
and line of business you serve:

- Letter type code: `PA-DECISION-EXTENSION`, category `Authorization`,
  audience `Member`. Mark it as a significant communication if your state
  requires translation and taglines.
- Merge fields the portal sends under `data`:

| Field | Value |
| --- | --- |
| `authorizationNumber` | The authorization number |
| `originalDueAt` | Original decision deadline (ISO 8601, UTC) |
| `extendedDueAt` | Extended decision deadline (ISO 8601, UTC) |
| `reason` | The reason the reviewer gave for the delay |
| `basis` | `EnrolleeRequested`, `ProviderRequested` or `PlanNeedsAdditionalInformation` |
| `grievanceRight` | `ExpeditedGrievance` (Medicare Advantage) or `Grievance` (Medicaid) |

The letter also carries `ruleRef`: 42 CFR 422.568(b) and 422.572(b) for
Medicare Advantage, or 42 CFR 438.210(d) and 438.404(c)(4) for Medicaid.

If no approved template applies, correspondence-service produces no letter.
The panel shows its reason and nothing is recorded, so the notice stays owed.

## Limitations

- The notice is addressed to the member's address on file. Authorized
  representatives and confidential-communication addresses are not resolved
  yet.
- Sending is a user action. Nothing sends the notice automatically, so owed
  notices must be worked from the metrics.
- An extension recorded only as sourced ledger evidence (from an external UM
  system) has no notice tracking here. The source system owns its notice.
