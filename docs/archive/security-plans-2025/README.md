# Archived: November 2025 Security Plans

> **Status:** Archived. Historical planning documents, kept for the record.
> Nothing here describes current state.

These two documents were written in November 2025, when Cloud Health Office ran on
Azure Logic Apps and AKS. They set out a 12-month security plan: Privileged Identity
Management, Defender for Cloud, a Q1 2026 third-party penetration test, SOC 2
readiness and a self-scored security posture.

The plan was not carried out as written. In particular:

- **No third-party penetration test has been performed.** Success criteria such as
  "Penetration test completed with no critical findings" were targets, not results.
- The posture scores (for example "8.3/10") were self-assessed and never
  independently reviewed.
- The platform has since moved to Kubernetes-first deployment on GKE and AKS, and
  the Azure-specific budget and tooling lines no longer apply.

| Document | What it was |
| --- | --- |
| [SECURITY-AUDIT-SUMMARY.md](SECURITY-AUDIT-SUMMARY.md) | Executive summary of the November 2025 documentation set and its 12-month plan |
| [SECURITY-HARDENING-ROADMAP.md](SECURITY-HARDENING-ROADMAP.md) | The 12-month hardening roadmap, budget and milestones |

## Current sources

- [Security one-pager](../../diligence/SECURITY-ONE-PAGER.md): current-state
  security architecture and what is and is not on file
- [Open Security Challenge](../../security-challenge/README.md): the executable,
  scoped security test campaigns and their stated limits
- [SECURITY.md](../../../SECURITY.md): vulnerability reporting
