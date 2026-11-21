# SOC 2 roadmap

**Status as of:** November 21, 2026. No SOC 2 report exists. No compliance platform or auditor is engaged. This is the plan.

## Why SOC 2, and why not first

Health plans ask for a SOC 2 Type II report from any vendor that hosts their data. It is not a legal requirement: HIPAA requires the safeguards and a BAA, not an audit report. A plan can sign without one if the security review is satisfied, and early-stage vendors often get through with a Type I, a penetration test letter and a completed questionnaire, with a Type II committed by a date in the contract.

So the order is:

1. HIPAA safeguards in place and adopted (they are the substance, and SOC 2 tests most of them anyway).
2. SOC 2 Type I: an auditor confirms the controls are designed properly on one date.
3. SOC 2 Type II: an auditor confirms the controls operated over a window of three to twelve months.

HITRUST (e1 or i1) is the healthcare-specific alternative some plans prefer. It costs more and takes longer. Do it only if a signed or near-signed plan requires it.

## Scope

| Choice | Recommendation | Why |
| --- | --- | --- |
| Trust Services Criteria | Security (required) plus Availability and Confidentiality | Security is the common criteria every report has. Availability matters because CMS-0057-F APIs have uptime and prior authorization has decision deadlines. Confidentiality covers PHI. Processing Integrity and Privacy add cost with little payer benefit at this stage |
| System in scope | The hosted HIPAA environment on Google Cloud, the deploy pipeline (GitHub Actions `hosted` environment), and the company's people and devices | This is where plan data lives. The demo and the source-available repository are out of scope, except as the change management path |
| Not in scope | Plans that host CHO themselves (Mode A) | Their own program applies. They get the source and the hardening guides instead |
| Subservice organization | Google Cloud, carved out | Google's own SOC 2 report covers the data centers and managed services. We rely on it and review it yearly |

## Timeline

Month 0 is when the hosted environment is applied (phase 1 in the [readiness plan](README.md)). The audit can't start before the system it describes exists.

| Month | Step |
| --- | --- |
| Before 0 | Pick the compliance platform. Connect Google Workspace, GitHub and the laptops. Load the adopted HIPAA policies; the platform's templates fill the SOC 2 gaps (vendor management, change management, asset inventory, background checks, code of conduct) |
| 0 | Apply the hosted environment. Connect the hosted Google Cloud project to the platform |
| 0 to 2 | Readiness. Close the platform's failing tests. Run the first quarterly access review, the restore drill and the penetration test. Collect evidence |
| 2 to 3 | Type I audit fieldwork, then the report (about two to four weeks after fieldwork) |
| 3 to 6 (or 9) | Type II observation window. A three-month first window is common and auditors accept it for a first report. Controls must run without gaps: access reviews, alert acknowledgements, change approvals, training, vendor reviews |
| 6 to 7 (or 10) | Type II fieldwork and report |
| Each year after | A twelve-month Type II window, back to back |

So a first Type II report is about seven months after the environment exists, if the first window is three months.

## Cost

Figures are 2026 list or observed ranges, from vendor and third-party pricing pages. Treat them as estimates until quoted.

| Item | First year | Later years | Notes |
| --- | --- | --- | --- |
| Compliance platform (Vanta or Drata) | $10K to $25K | Same | Vanta's smallest published package for 1 to 20 employees is about $14K a year; Drata's smallest is about $15K. Observed medians are $20K to $25K. Adding HIPAA as a second framework is often about $7.5K more, and may be worth it because plans ask for both |
| SOC 2 Type I audit | $7.5K to $15K | n/a | Startup-focused CPA firms, often through the platform's auditor network. A Big Four firm costs several times more and adds little at this stage |
| SOC 2 Type II audit | $12K to $20K | $12K to $20K | Per window |
| Penetration test | $10K to $25K | Same, yearly | Web app, API and cloud configuration of the hosted endpoints. SOC 2 doesn't strictly require one, but every plan asks, and the [risk analysis](../hipaa-hosted/RISK-ANALYSIS.md) plans one (item 4.5) |
| Counsel review of policies and BAA | $5K to $15K | Small | Once, then for each plan's paper |
| Cyber liability insurance | $3K to $10K | Same | Plans commonly require $1M to $5M of coverage in the contract. Quote needed |
| Background checks, security keys, MDM | Under $2K | Small | Two people today |
| **Total, first 12 months** | **About $50K to $110K** | **About $40K to $90K** | Excludes the hosted environment's run cost (about $1K to $1.5K a month, see [HOSTED-ENVIRONMENT-APPLY.md](HOSTED-ENVIRONMENT-APPLY.md)) |

The older estimate in [THIRD-PARTY-AUDIT-PROCESS.md](../../security/THIRD-PARTY-AUDIT-PROCESS.md) ($30K to $80K for Type II, Big Four auditors) assumed a larger firm. A startup-tier auditor is enough for a first report and is what plans see from vendors this size.

## Vanta or Drata

Both do the same core job: connect to the cloud, identity, code and devices; test controls continuously; hold policies and evidence; give the auditor one place to look; and publish a trust page. Either is fine. The choice comes down to price and how well each reads the Google Cloud setup.

| Question | What to check in the demo |
| --- | --- |
| Google Cloud coverage | Does it read GKE, Cloud KMS, organization policies, Cloud Logging buckets and Backup for GKE, or only Compute Engine and Cloud Storage? Most of our controls live in the first group |
| GitHub | Branch protection, required reviews, the `hosted` environment's required reviewers, Dependabot and code scanning alerts |
| HIPAA framework | Is HIPAA included or priced as an extra framework? Plans want both mapped |
| Auditor network | Startup-rate auditors who do Type I and Type II with the same firm |
| Startup pricing | Both have startup and accelerator discounts; ask, and ask for a multi-year price |
| Trust center | A page to send plans the reports and policies under NDA |

Lower-cost alternatives exist (Secureframe, Sprinto, Thoropass, which bundles the audit). Compare at least three quotes.

Recommendation: start trials of Vanta and Drata in the same week, pick on price and Google Cloud coverage, and sign once the hosted environment is about to be applied, so the first paid months aren't spent on an empty project.

## What SOC 2 will find today

These are the gaps an auditor or the platform would flag first. Each is cheap to close.

| Gap | Fix |
| --- | --- |
| Policies are drafts | Adopt and sign [hipaa-hosted/](../hipaa-hosted/README.md); add the platform's policies for change management, vendor management, asset inventory, acceptable use, code of conduct and data retention |
| No named Security Officer | Name one (the CTO is the natural choice, with the CEO as backup) |
| No background check policy | Adopt one; run checks for anyone who gets access to the hosted environment |
| No device management | Lightweight MDM or the platform's device agent on every company laptop: disk encryption, screen lock, OS updates |
| No vendor inventory or reviews | List vendors (Google Cloud, GitHub, Cloudflare for the website only, email, Anthropic if used) with their reports and BAAs |
| Branch protection and required reviews not shown | Confirm and export the `main` branch rules and the `hosted` environment's required reviewers as evidence |
| One-person approvals | With two people, a deploy needs the other's approval. Document the exception if only one person can review |
| Portal idle timeout of 2 hours | Lower it to 15 to 30 minutes (see [HIPAA-POSTURE.md](HIPAA-POSTURE.md)) |
| No failed sign-in alerting inside CHO | Already the next item in workstream 6 of [GCP-HOSTED-HIPAA.md](../../deploy/GCP-HOSTED-HIPAA.md) |
