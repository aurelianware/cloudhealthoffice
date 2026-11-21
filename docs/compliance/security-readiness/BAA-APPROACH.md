# BAA approach

**Status as of:** November 21, 2026. Not legal advice. Counsel reviews every term below before we offer it.

## Three agreements, in this order

| Agreement | Between | Status |
| --- | --- | --- |
| Google Cloud HIPAA Business Associate Addendum | Aurelianware and Google | Click-through acceptance in the hosted project's console (IAM & Admin, Privacy). Free. Not yet accepted, because the hosted project doesn't exist yet. Accept it before the first apply ([HOSTED-ENVIRONMENT-APPLY.md](HOSTED-ENVIRONMENT-APPLY.md)) |
| Plan BAA | The plan (covered entity) and Aurelianware (business associate) | [Template](../../diligence/BAA-TEMPLATE.md) drafted, not reviewed by counsel |
| State Medicaid flow-down | The plan's Medicaid contract terms passed on to Aurelianware as a subcontractor | For Texas Medicaid, the HHS Data Use Agreement's subcontractor terms. Not yet seen from a plan |

Other vendors that could touch PHI each need their own BAA first, or must be kept out of the PHI path:

| Vendor | In the PHI path? | What we do |
| --- | --- | --- |
| Google Cloud | Yes | BAA (above). Use only BAA-covered products |
| Anthropic (AI claims examiner) | Only if turned on | The hosted deploy refuses the examiner until `ANTHROPIC_BAA_CONFIRMED=true`. Turn it on only after a BAA with Anthropic is signed and the plan agrees |
| GitHub | No | No PHI in the repository, issues or Actions logs (policy and CI PHI checks) |
| Cloudflare | No | Website and demo only. Hosted DNS records are DNS only, not proxied, so Cloudflare never decrypts plan traffic |
| Email and chat (Google Workspace, Slack or similar) | Should be no | Policy says no PHI in email or chat. Google Workspace has its own BAA; accept it anyway as a backstop, since a plan may email a file by mistake |
| Compliance platform (Vanta or Drata) | No | It reads configuration and metadata, not member data. Confirm in its data processing terms |

## Whose paper

Health plans almost always send their own BAA. Expect that from a Medicaid plan such as PCHP, which is part of a public hospital system and has standard vendor paper. Our template is for plans that don't have one, and it is our checklist for reviewing theirs.

When the plan's paper arrives, counsel checks it against this list.

## What we can give, and what to push back on

| Term | Our position | Why |
| --- | --- | --- |
| Breach notice to the plan | Yes to short deadlines we can meet: an initial notice within 24 hours of discovery, a written report within 3 business days, and updates as we learn more | The Texas DUA requires these clocks anyway (below). The template's 15 days is fine for commercial plans |
| Security incident notice (not a breach) | Aggregate reporting of unsuccessful attempts (scans, blocked logins) on a schedule; prompt notice of successful ones | Without this carve-out every blocked scan is a reportable event |
| Who notifies members, HHS, the AG and the media | The plan, unless it delegates to us. We pay reasonable costs of a breach we caused | Standard. Cap the cost obligation with the liability terms |
| Indemnity and liability | Mutual, capped (often 1 to 3 times the fees, or the insurance limit for data breaches) | An uncapped indemnity is the term most likely to hurt a startup. Insurance sets the realistic cap |
| Cyber insurance | Carry $1M to $5M of cyber liability; name the plan as additional insured if asked | Plans ask for it. Quote needed |
| Offshore access | Agree: no PHI stored, processed or viewed outside the United States | True today: US-only organization policy, no offshore staff or subcontractors |
| Subcontractors | Name Google Cloud; 30 days' notice before adding one that receives PHI | In the template |
| Audit rights | Yes to questionnaires, reports (SOC 2 when we have it) and a reasonable on-site or remote audit once a year on notice | Unlimited audits at our cost are a push-back |
| Return or destruction at the end | Yes, within 30 to 90 days; backups expire on their 35-day cycle; audit logs kept six years as HIPAA documentation | In the template |
| Data aggregation and de-identification for our own use | Don't ask for it now | Asking slows signature and isn't needed for the pilot |
| Encryption | Yes: at rest and in transit, as built | Built in the hosted profile |
| Term and termination for cause | Standard 30-day cure | In the template |

## Texas Medicaid flow-down (PCHP and similar plans)

A Texas Medicaid or CHIP managed care organization holds state confidential information under its contract with Texas Health and Human Services, which includes the HHS Data Use Agreement (DUA, version 8.5 is the current public form). The DUA requires the plan to bind its subcontractors to the same terms, either with the DUA's Attachment 1 or the plan's own equivalent, and makes the plan responsible for them. So expect the plan to send its DUA flow-down with its BAA.

What the DUA (v8.5) asks of a contractor, and what it means for us:

| DUA term | What it says | Our readiness |
| --- | --- | --- |
| Initial breach notice | Within the first consecutive clock hour of discovery for federal data from a federal system of records (which it says includes Medicaid confidential information), otherwise within 24 hours | Our incident procedure must be able to notify the plan within one hour of discovery, around the clock. Today it gives a heads-up within 2 business days. **Update the procedure, the alert routing and the on-call rota before signing** |
| Written report | By 5 p.m. on the third business day after discovery, then updates at least every three business days | Matches the 3-business-day position above. Add the template to the incident log |
| Encryption at rest | Required unless other safeguards are adequate; proof within 48 hours of a request | Built. The Terraform and the KMS key list are the proof |
| Training | Confidentiality, privacy and security training for workforce and subcontractors, with evidence on request | Drafted training record. Add a short Texas section (Health and Safety Code chapter 181) |
| Security and Privacy Initial Inquiry (SPI) | A NIST SP 800-53 based questionnaire returned with the contract | The plan may pass it to us or answer it for us. Our answers in [PAYER-SECURITY-QUESTIONNAIRE.md](PAYER-SECURITY-QUESTIONNAIRE.md) cover most of it |
| Policies on request | Within three business days | Keep adopted copies ready in the compliance store |
| Periodic confirmation of compliance | Written confirmation when asked | The yearly evaluation in [POLICIES.md](../hipaa-hosted/POLICIES.md) section 8 produces it |

Texas law adds its own duty: a business that maintains computerized sensitive personal information for another must notify the owner immediately after discovering a breach (Business and Commerce Code 521.053). The one-hour DUA clock is the stricter of the two, so meeting it meets both.

TX-RAMP, the state's cloud security certification, applies to cloud services contracted by Texas state agencies. A Medicaid plan is not a state agency, so TX-RAMP shouldn't apply to us as the plan's vendor. Confirm with the plan, because some Texas public entities (hospital districts, for example) choose to require it.

## What changes in the template

This plan adds a state Medicaid rider to the [BAA template](../../diligence/BAA-TEMPLATE.md): the 24-hour and 1-hour initial notice, the 3-business-day written report, the DUA flow-down by reference, and the US-only commitment. The rider is used when the plan is a Medicaid or CHIP plan, and is checked against the plan's actual DUA.

## Steps

1. Counsel reviews the template and this approach (with the policies, one engagement).
2. Accept the Google Cloud BAA when the hosted project is created. Accept the Google Workspace BAA now.
3. Update the incident procedure for the one-hour and 24-hour clocks, and route security alerts to a phone, not only email.
4. Get a cyber insurance quote.
5. When a plan engages: ask for its BAA and its DUA flow-down early, in the first security call, since legal review is usually the long pole.
