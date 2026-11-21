# Business Associate Agreement — Template

**Status:** TEMPLATE. Not executed. Not legal advice.
**Parties (as executed):** Covered Entity = Customer; Business Associate = Aurelianware, Inc. (“CHO”)
**Use:** founding-partner CMS-0057-F Compliance Accelerator pilots
**Must be reviewed by both parties’ counsel before signature.**

This template is a starting draft for HIPAA Business Associate terms. It is not a substitute for counsel. Do not paste production PHI into Cloud Health Office until a version of this agreement (or the customer’s paper) is signed.

---

**This Business Associate Agreement** (“BAA”) is entered into as of ____________ (“Effective Date”) by and between:

**Covered Entity:** ________________________________ (“Covered Entity”)
**Business Associate:** Aurelianware, Inc., a ________ corporation (“Business Associate”)

## 1. Purpose

Covered Entity is engaging Business Associate to provide the Cloud Health Office CMS-0057-F Compliance Accelerator and related Layer 1 services described in the Order Form. Those services may involve creating, receiving, maintaining, or transmitting Protected Health Information (“PHI”) only after this BAA is effective and only in the environment named on the Order Form.

Until this BAA is signed, the engagement uses **synthetic data only**.

## 2. Definitions

Terms used but not defined here (including PHI, Electronic PHI, Unsecured PHI, Breach, Security Incident, and Required By Law) have the meaning given in HIPAA (45 C.F.R. Parts 160 and 164).

**“Services”** means the services in the Order Form, including FHIR APIs, SMART-on-FHIR enforcement, prior-authorization surfaces, audit logging, and implementation assistance.

**“Pilot Environment”** means the environment identified on the Order Form: either the Business Associate–hosted environment on Google Cloud (a dedicated project in the United States, described in `docs/deploy/GCP-HOSTED-HIPAA.md`), or a Kubernetes environment the Covered Entity hosts.

## 3. Permitted uses and disclosures

Business Associate may use or disclose PHI only:

- (a) to perform the Services;
- (b) as Required By Law;
- (c) for Business Associate’s proper management and administration, provided any third-party disclosure is Required By Law or the recipient is bound to confidentiality;
- (d) as otherwise authorized in writing by Covered Entity.

Business Associate shall not use or disclose PHI in a manner that would violate HIPAA if done by Covered Entity, except as permitted above.

Minimum necessary applies to Business Associate’s internal use.

## 4. Safeguards

Business Associate shall:

- implement administrative, physical, and technical safeguards that reasonably and appropriately protect Electronic PHI;
- maintain tenant isolation for the Pilot Environment;
- not use production PHI in logs, screenshots, issue trackers, or the public GitHub repository;
- ensure workforce members with PHI access are authorized and trained;
- report Security Incidents and Breaches as in Section 6;
- for the Business Associate–hosted environment, maintain the safeguards described in `docs/compliance/hipaa-hosted/` (risk analysis, policies, incident response, contingency plan, training and quarterly access review) and make the adopted versions available to Covered Entity on request.

Covered Entity acknowledges that Cloud Health Office is source-available. Source availability does not authorize Covered Entity to publish PHI, credentials, or production configurations.

## 5. Subcontractors

Business Associate shall ensure that any subcontractor that creates, receives, maintains, or transmits PHI on its behalf agrees to restrictions and conditions at least as protective as this BAA. For the Business Associate–hosted environment the cloud subcontractor is Google Cloud, under Google's HIPAA Business Associate Addendum, with PHI kept in the United States. Where the Order Form names an Azure environment, the cloud subcontractor is Microsoft Azure. Any other subcontractor that would receive PHI (for example an AI model provider for the advisory claims examiner) is used only after it signs a business associate agreement and is listed on the Order Form. Business Associate gives Covered Entity thirty (30) days' notice before adding a subcontractor that receives PHI.

## 6. Breach and security incident notice

Business Associate shall notify Covered Entity without unreasonable delay and in no case later than **fifteen (15) calendar days** after discovery of a Breach of Unsecured PHI, and shall provide the information reasonably available that Covered Entity needs for 45 C.F.R. § 164.404 notices.

Security Incidents that do not constitute a Breach (for example, unsuccessful port scans routinely logged) may be reported in aggregate on a mutually agreed cadence unless Covered Entity requests otherwise in writing.

## 7. Individual rights and accounting

Business Associate shall, in the time and manner reasonably specified by Covered Entity, make available PHI in a Designated Record Set for access, amendment, and accounting of disclosures, to the extent the Services hold such a set. Layer 1 FHIR Patient Access is a Covered Entity-facing API; Covered Entity remains responsible for member identity proofing and for deciding what is in the Designated Record Set.

## 8. Access by HHS

Business Associate shall make its internal practices, books, and records relating to PHI use and disclosure available to the Secretary of HHS for determining HIPAA compliance.

## 9. Term, return, and destruction

This BAA is effective on the Effective Date and continues until the Services terminate.

Upon termination, Business Associate shall return or destroy PHI remaining in its possession, if feasible, and retain no copies except as Required By Law or for dispute, audit, or backup cycles that are then purged on the documented schedule (for the Business Associate–hosted environment, backups expire 35 days after they are taken; audit logs that record access are kept six years as HIPAA documentation requires). If return or destruction is infeasible, Business Associate shall extend this BAA’s protections to the remaining PHI and limit further use to the purposes that make return infeasible.

## 10. Termination for cause

Covered Entity may terminate the Order Form and this BAA if Business Associate has materially breached HIPAA obligations and has not cured within thirty (30) days of written notice, or immediately if cure is not possible.

## 11. No agency; no attestation

Nothing in this BAA makes Business Associate the Covered Entity’s compliance officer or attests that the Covered Entity meets CMS-0057-F. Production attestation remains Covered Entity’s.

## 12. Order of precedence

If this BAA conflicts with the MSA or Order Form regarding PHI, this BAA controls. Commercial license terms in the Order Form control production use under BSL 1.1.

## 13. State Medicaid rider (use when Covered Entity is a Medicaid or CHIP plan)

_Drafting note: include this section only for a Medicaid or CHIP plan, and check each term against the plan's own state contract and Data Use Agreement. The terms below follow the Texas HHS Data Use Agreement (v8.5). See `docs/compliance/security-readiness/BAA-APPROACH.md`._

- (a) **Flow-down.** Business Associate agrees to the subcontractor terms of Covered Entity's Data Use Agreement with the state Medicaid agency, attached as Exhibit A, for any state confidential information it receives under the Services. Where Exhibit A is stricter than this BAA, Exhibit A controls.
- (b) **Initial notice.** Business Associate shall notify Covered Entity of a discovered or suspected Breach or Security Incident involving state confidential information within one (1) clock hour of discovery for federal data from a federal system of records, including Medicaid confidential information, and within twenty-four (24) hours of discovery otherwise, by phone and email to the contacts Covered Entity names.
- (c) **Written report.** Business Associate shall deliver a written report by 5 p.m. local time on the third (3rd) business day after discovery, and updates at least every three (3) business days until Covered Entity agrees the matter is closed.
- (d) **Location.** Business Associate shall not store, process, access or view PHI or state confidential information outside the United States, and shall not use offshore workforce or subcontractors for the Services.
- (e) **Evidence.** Business Associate shall provide its security and privacy policies within three (3) business days of a request, proof of encryption within forty-eight (48) hours of a request, and evidence of workforce training on request.

## Signature

Covered Entity: ___________________________ Date: ________
Name / Title: _____________________________

Business Associate (Aurelianware, Inc.): ________________ Date: ________
Name / Title: _____________________________
