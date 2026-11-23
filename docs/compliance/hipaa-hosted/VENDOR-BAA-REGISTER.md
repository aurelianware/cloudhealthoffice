# Vendor and subcontractor BAA register

**Status:** DRAFT, checked against the repository on November 23, 2026. No vendor BAA has been signed or accepted yet. The Security Officer keeps the live copy, with signed agreements and dates, in the private compliance store.
**Rule:** 45 CFR 164.308(b) and 164.502(e)(1)(ii): a business associate may let a subcontractor create, receive, maintain or transmit PHI only under a written BAA.

Every vendor falls into one of three groups. **A**: in the PHI path, BAA required before first PHI. **B**: could receive PHI by mistake, so get a BAA anyway as a backstop where one is offered. **C**: kept out of the PHI path by design and policy; no BAA, and the rule that keeps PHI out is listed.

## A. In the PHI path

| Vendor | What it does with PHI | Agreement | Status | Who acts |
| --- | --- | --- | --- | --- |
| Google Cloud | Hosts the whole hosted environment: cluster, disks, buckets, keys, logs, backups, load balancer | Google Cloud HIPAA Business Associate Addendum, accepted in the console for the hosted project | **Not accepted.** The hosted project doesn't exist yet | Account owner, at project creation |
| Anthropic (optional) | The advisory AI claims examiner sends claim data to the model | A BAA with Anthropic, negotiated through its sales team (not a click-through) | Not signed. The hosted deploy refuses the examiner unless `ANTHROPIC_BAA_CONFIRMED=true` | An officer, only if a plan wants the examiner |

### Google Cloud services the hosted profile uses

The Google BAA covers only the products on Google's [HIPAA covered products list](https://docs.cloud.google.com/docs/security/compliance/hipaa). The hosted profile (`infrastructure/gcp` with `hosted = true`) uses these; confirm each is on the list the day the BAA is accepted, and again each year:

| Service | Holds PHI? | Used for |
| --- | --- | --- |
| Google Kubernetes Engine and Compute Engine (nodes, persistent disks) | Yes | Every CHO service, MongoDB, Kafka, Redis |
| Cloud Storage | Yes | Documents, CPCDS file drops, bulk export files |
| Backup for GKE | Yes | Daily backups of every volume |
| Cloud Logging | Must not, but treated as if it might | Audit logs (six-year bucket), application logs |
| Cloud Load Balancing, Cloud Armor, Identity-Aware Proxy | In transit | Ingress, WAF, staff sign-in |
| Cloud KMS (HSM) | No (keys) | Customer-managed encryption keys |
| Secret Manager | No (passwords) | Database passwords |
| Artifact Registry | No (images) | Service images |
| Cloud Monitoring | No | Security alerts on audit log entries |
| VPC, Cloud NAT, IAM, Organization Policy | No | Network and access control |

Two Google services are **refused** by the hosted profile because they aren't covered by the profile's encryption keys yet: the managed FHIR store copy (Cloud Healthcare API, BigQuery) and Vertex AI. Don't turn them on in the hosted project without updating this register and the risk analysis.

## B. Backstops: get a BAA where offered

| Vendor | Why PHI could reach it | Agreement | Status | Who acts |
| --- | --- | --- | --- | --- |
| Google Workspace (email, Drive) | A plan may email a file by mistake. Drive is the proposed private compliance store, which holds incident logs that may describe PHI | Google Workspace BAA, accepted in the Admin console (Account, Legal and compliance) | Not accepted | Workspace super admin. Free, a few minutes |
| Outside counsel | Reviews a suspected breach | BAA or confidentiality terms in the engagement letter | Not engaged | An officer, when counsel is engaged |
| Forensic or incident response firm | Investigates a breach in the environment | BAA in the engagement; often arranged through the cyber insurer's panel | Not engaged | Security Officer, during an incident |
| Penetration test firm | Tests the hosted endpoints | Test against a synthetic tenant before first PHI, so no BAA is needed. If testing happens after PHI arrives, sign a BAA first | Not engaged | Security Officer |

## C. Kept out of the PHI path

| Vendor | What it does | What keeps PHI out |
| --- | --- | --- |
| GitHub | Source code, issues, pull requests, Actions | Policy (no PHI in the repository, issues, chat or screenshots); PHI checks in CI; the hosted deploy authenticates with Workload Identity Federation and runs no data through Actions logs |
| Demo cluster and demo Google Cloud project | Public demo and Inferno runs | Synthetic data only; a separate project with no shared keys, buckets or credentials; the hosted deploy refuses a demo cluster context and the demo tenant |
| Cloudflare | Website, demo tunnel and Access | Not used by the hosted environment. Hosted DNS records are DNS only, never proxied, so Cloudflare never decrypts plan traffic |
| Email delivery from the portal | Sales inquiry notifications only (`SmtpEmailNotificationService`) | No PHI in those messages. CHO letters are stored in the documents bucket, not emailed. Before any feature emails member data, the email provider needs a BAA |
| Observability and APM tools (Datadog, Grafana Cloud, Sentry and similar) | Not used | Telemetry stays in Google Cloud. The OpenTelemetry collector exports nowhere outside the cluster by default, and a PHI-scrubbing processor runs in every service. Adding an outside tool needs a BAA and an update here first |
| Compliance platform (Vanta, Drata or similar) | Reads cloud, GitHub and device configuration | Configuration and metadata only. Confirm in its data processing terms before connecting the hosted project |
| Chat (Slack or similar), password manager, CRM | Company tools | Policy: no PHI. Training covers it |
| Bank (NACHA payment files) | Provider payments from payment-service | Payment processing by a financial institution is generally outside HIPAA's business associate rules. Confirm with counsel what the payment addenda carry |

The plan's own vendors (its clearinghouse, its core system host, its print and mail house) are the plan's business associates, not ours. CHO exchanges files with them only through the plan.

## Adding a vendor

1. Decide its group. If it could create, receive, maintain or transmit PHI, it is group A.
2. For group A, sign the BAA first. Then update the [risk analysis](RISK-ANALYSIS.md) and the subcontractor list in [POLICIES.md](POLICIES.md) section 7.
3. Give each plan the notice its BAA requires (30 days in our template) before the vendor receives that plan's PHI.
4. Record the date, the agreement and the reviewer here.

## Yearly review

Each year, and when a vendor changes its terms: check that each group A agreement is still in force, re-check Google's covered products list against the services above, and read Google's current SOC 2 report summary. Record the review date here.

| Review date | Reviewer | Changes |
| --- | --- | --- |
| | | |
