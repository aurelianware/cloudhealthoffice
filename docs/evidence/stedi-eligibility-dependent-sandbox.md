# Stedi dependent eligibility sandbox validation

Date: 2026-08-23 UTC

Branch: `feat/stedi-eligibility-dependents`

Commit: `774b8d8dc6c271b5e7a823970ac37e43158cf027`

Test: `StediLiveSmokeTests.Sandbox_DependentEligibility_ReturnsActiveCoverage`

Stedi endpoint: `POST https://healthcare.us.stedi.com/2024-04-01/change/medicalnetwork/eligibility/v3`

| Field | Value |
| --- | --- |
| Stedi application mode | test |
| Trading partner | 87726 (UnitedHealthcare) |
| Synthetic fixture | Subscriber John Doe / UHC202649; dependent Jane Doe DOB 1952-11-21; NPI 1999999984; service type 30 |
| HTTP | 200 |
| Gateway transaction status | Completed |
| Error category | None |
| Coverage | Active Coverage |
| Approximate round-trip | ~1.0 s (direct Stedi) / live gateway smoke |

No API keys, auth headers, raw 270/271 payloads, or PHI logs are recorded here.
The fixture is Stedi's published synthetic sandbox data.
