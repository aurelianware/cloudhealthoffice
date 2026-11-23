# Security review readiness plan

**Status as of:** November 21, 2026. A plan, not an attestation. Nothing here is adopted, audited or applied yet.
**Goal:** a health plan's security review is already answered, and the hosted environment is already built, so Cloud Health Office (CHO) can take real data within days of a plan signing, not months.
**Owner:** Aurelianware, Inc. (Security Officer to be named; see [POLICIES.md](../hipaa-hosted/POLICIES.md)).

## Where things stand

| Area | Done | Not done |
| --- | --- | --- |
| Hosted HIPAA environment | Terraform for the `hosted` profile on Google Cloud: HSM-backed customer-managed keys, private regional cluster, US-only and no-key organization policies, six-year audit log bucket, Cloud Armor, IAP, daily backups with a delete lock, security alerts. A reviewed deploy workflow. Validated in CI (`terraform-gcp.yml`) and by stub tests | Never applied. No Google Cloud organization, hosted project or Google BAA yet. Restore drill never run. See [HOSTED-ENVIRONMENT-APPLY.md](HOSTED-ENVIRONMENT-APPLY.md) |
| HIPAA administrative safeguards | Drafts of the risk analysis, policies, incident response, contingency plan, training record and quarterly access review in [hipaa-hosted/](../hipaa-hosted/README.md) | Not adopted. No Security Officer named, no counsel review, no signatures, no training records |
| Application security | Token-only tenant resolution, permission on every endpoint, SMART on FHIR scopes, record access audit log, PHI scrubbing in telemetry. Trivy, gitleaks, npm audit, .NET vulnerable package checks, Dependabot and CodeQL | No independent penetration test. No failed sign-in alerting in CHO itself. Portal session idle timeout is 2 hours |
| BAA | Plan-facing [BAA template](../../diligence/BAA-TEMPLATE.md), naming Google Cloud as the subcontractor | No counsel review. No executed BAA with a plan or with Google. Texas Medicaid terms added in this plan, not yet reviewed. See [BAA-APPROACH.md](BAA-APPROACH.md) |
| SOC 2 | Plan in [SOC2-ROADMAP.md](SOC2-ROADMAP.md) | No compliance platform, no auditor, no report. The website says SOC 2 is planned, not held |
| Security questionnaire | Draft answers in [PAYER-SECURITY-QUESTIONNAIRE.md](PAYER-SECURITY-QUESTIONNAIRE.md) | Several answers are "planned" or "not yet" and say so |

The full HIPAA posture, safeguard by safeguard: [HIPAA-POSTURE.md](HIPAA-POSTURE.md).

## The documents

| Document | Who reads it |
| --- | --- |
| [BAA-READY-CHECKLIST.md](BAA-READY-CHECKLIST.md) | Founders: every step from today to signing and honoring a plan's BAA, with what only an officer can do, and cyber insurance |
| [SOC2-ROADMAP.md](SOC2-ROADMAP.md) | Founders and investors: Type I then Type II, timeline, cost, Vanta or Drata |
| [HIPAA-POSTURE.md](HIPAA-POSTURE.md) | A plan's CISO or privacy officer: each Security Rule safeguard, what implements it, and its status |
| [BAA-APPROACH.md](BAA-APPROACH.md) | Founders and counsel: whose paper, what to give and refuse, Texas Medicaid flow-downs, our own BAAs with vendors |
| [PAYER-SECURITY-QUESTIONNAIRE.md](PAYER-SECURITY-QUESTIONNAIRE.md) | The plan's security reviewer: pre-filled answers to the questions a Texas Medicaid plan such as PCHP sends |
| [HOSTED-ENVIRONMENT-APPLY.md](HOSTED-ENVIRONMENT-APPLY.md) | The person who applies the environment: decisions, accounts, order of steps, run cost |

## Sequence

The order matters more than the dates. Each phase starts when the one before is done, or when a plan's interest makes it worth the spend.

| Phase | When | What | Rough cost |
| --- | --- | --- | --- |
| 0. Paper | Now, before any plan signs | Name the Security and Privacy Officer. Counsel reviews the policies and BAA template. Adopt and sign the policies. Turn on 2-step verification with security keys for every company account. Pick a compliance platform. Fix the portal idle timeout | $5K to $15K counsel; platform from $10K a year |
| 1. Build | When a plan is in late-stage talks (security review started), or once the raise closes | Create the Google Cloud organization and hosted project, accept the Google BAA, apply the hosted profile, run the restore drill, tune then enforce the WAF, commission a penetration test, start SOC 2 Type I readiness | About $1K to $1.5K a month to run; $10K to $25K penetration test |
| 2. Sign | Plan signs the order form, BAA and any state flow-downs | Add the plan's tenant, admins, IAP members, SFTP key and DNS. First file drop. See [HOSTED-ENVIRONMENT-APPLY.md](HOSTED-ENVIRONMENT-APPLY.md), "After a plan signs" | Included in phase 1 |
| 3. Attest | About three months after phase 1 | SOC 2 Type I report, then a three to six month Type II window | $7.5K to $15K Type I; $12K to $20K Type II |

With phases 0 and 1 done, phase 2 is a few days of configuration. Without them, a plan's security review typically takes two to four months, and the environment build adds more.

## What we never claim

Until each item exists, we do not say "SOC 2 compliant", "HIPAA certified" (there is no such certification), "HITRUST", "pen tested" or "production-ready". Questionnaire answers say "planned" or "not yet" where that is the truth. See the guardrails in [docs/compliance/README.md](../README.md).
