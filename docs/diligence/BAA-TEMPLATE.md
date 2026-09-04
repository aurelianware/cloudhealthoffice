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

**“Pilot Environment”** means the Kubernetes / AKS (or equivalent) tenant identified on the Order Form.

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
- report Security Incidents and Breaches as in Section 6.

Covered Entity acknowledges that Cloud Health Office is source-available. Source availability does not authorize Covered Entity to publish PHI, credentials, or production configurations.

## 5. Subcontractors

Business Associate shall ensure that any subcontractor that creates, receives, maintains, or transmits PHI on its behalf agrees to restrictions and conditions at least as protective as this BAA. The default cloud subprocessor for a standard pilot is Microsoft Azure. Additional subprocessors are listed on the Order Form.

## 6. Breach and security incident notice

Business Associate shall notify Covered Entity without unreasonable delay and in no case later than **fifteen (15) calendar days** after discovery of a Breach of Unsecured PHI, and shall provide the information reasonably available that Covered Entity needs for 45 C.F.R. § 164.404 notices.

Security Incidents that do not constitute a Breach (for example, unsuccessful port scans routinely logged) may be reported in aggregate on a mutually agreed cadence unless Covered Entity requests otherwise in writing.

## 7. Individual rights and accounting

Business Associate shall, in the time and manner reasonably specified by Covered Entity, make available PHI in a Designated Record Set for access, amendment, and accounting of disclosures, to the extent the Services hold such a set. Layer 1 FHIR Patient Access is a Covered Entity-facing API; Covered Entity remains responsible for member identity proofing and for deciding what is in the Designated Record Set.

## 8. Access by HHS

Business Associate shall make its internal practices, books, and records relating to PHI use and disclosure available to the Secretary of HHS for determining HIPAA compliance.

## 9. Term, return, and destruction

This BAA is effective on the Effective Date and continues until the Services terminate.

Upon termination, Business Associate shall return or destroy PHI remaining in its possession, if feasible, and retain no copies except as Required By Law or for dispute, audit, or backup cycles that are then purged on the documented schedule. If return or destruction is infeasible, Business Associate shall extend this BAA’s protections to the remaining PHI and limit further use to the purposes that make return infeasible.

## 10. Termination for cause

Covered Entity may terminate the Order Form and this BAA if Business Associate has materially breached HIPAA obligations and has not cured within thirty (30) days of written notice, or immediately if cure is not possible.

## 11. No agency; no attestation

Nothing in this BAA makes Business Associate the Covered Entity’s compliance officer or attests that the Covered Entity meets CMS-0057-F. Production attestation remains Covered Entity’s.

## 12. Order of precedence

If this BAA conflicts with the MSA or Order Form regarding PHI, this BAA controls. Commercial license terms in the Order Form control production use under BSL 1.1.

## Signature

Covered Entity: ___________________________ Date: ________
Name / Title: _____________________________

Business Associate (Aurelianware, Inc.): ________________ Date: ________
Name / Title: _____________________________
