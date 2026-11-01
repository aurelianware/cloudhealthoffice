# Quarterly access and activity review: hosted environment

**Status:** DRAFT template. The Security Officer runs it each quarter (January, April, July, October) and keeps the completed copy in the private compliance store for six years.
**Rule:** 45 CFR 164.308(a)(1)(ii)(D) information system activity review; (a)(3) workforce clearance and termination; (a)(4) access authorization.

| Field | Value |
| --- | --- |
| Quarter | ________ |
| Reviewer | ________ |
| Date | ________ |

## Steps

**1. Google Cloud IAM.** List who holds which role on the hosted project:

```bash
gcloud projects get-iam-policy PROJECT --format=json \
  | jq -r '.bindings[] | .role as $r | .members[] | "\(.)\t\($r)"' | sort
```

For each human member, confirm that they are still in the workforce, that they still need the role, and that it is the narrowest role that works. Owner is held by at most two people. Every service account is one that Terraform made.

**2. IAP access.** List who can reach the portal:

```bash
gcloud iap web get-iam-policy --project=PROJECT
```

Confirm each person or group with the plan's contact, in writing.

**3. CHO tenant users.** In the portal, export the tenant's user list. Confirm each Active user and role with the plan's contact. Deactivate anyone the plan doesn't confirm.

**4. Secrets and keys.**
- The Secret Manager versions listed are the ones in use.
- Every KMS key is rotating: its next rotation time is in the future.
- No service account keys exist (the organization policy blocks them; confirm with `gcloud iam service-accounts keys list` for each account).

**5. Activity.** Review the past quarter's:
- IAM, KMS, logging and organization policy alerts: each one was expected or was handled as an incident
- backup results: no failures left unresolved
- data access audit logs: look for unusual volume, unfamiliar principals or after-hours admin access
- Cloud Armor logs: blocked and previewed rules (tune before enforcing)
- a sample of application logs: no ePHI payloads in them

**6. Training.** Everyone with access has a training record from the last twelve months.

**7. Off-boarding.** Everyone who left or changed role this quarter lost access the same business day.

## Findings

| # | Finding | Action | Owner | Due | Done |
| --- | --- | --- | --- | --- | --- |
| | | | | | |

Findings that point to a security incident go to [INCIDENT-RESPONSE.md](INCIDENT-RESPONSE.md).

Reviewer signature: ________________________ Date: ________
