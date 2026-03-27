# Cloud Health Office - Terms of Service

**Effective Date**: March 1, 2026  
**Last Updated**: February 17, 2026  
**Version**: 2.0

---

## 1. Definitions

**"Provider"** means Aurelianware, Inc and its Cloud Health Office platform, the entity providing the Services under this Agreement.

**"Service"** means the Cloud Health Office SaaS platform, including all EDI transaction processing, FHIR R4 APIs, Logic Apps workflows, Azure infrastructure, monitoring, and support services provided by Provider.

**"Customer"** means the healthcare organization, health plan, third-party administrator, or other entity that has entered into a subscription agreement with Provider to use the Services.

**"Party"** means either Provider or Customer individually. **"Parties"** means Provider and Customer collectively.

**"PHI"** has the meaning set forth in the Health Insurance Portability and Accountability Act of 1996 and its implementing regulations at 45 CFR §160.103 (collectively, "HIPAA").

**"Covered Entity"** has the meaning set forth in HIPAA at 45 CFR §160.103.

**"Business Associate"** has the meaning set forth in HIPAA at 45 CFR §160.103.

**"Subscription Term"** means the initial term or renewal term during which Customer has an active paid subscription to the Services.

**"Authorized Users"** means Customer's employees, contractors, and agents who are authorized by Customer to access and use the Services.

**"Documentation"** means the user guides, technical specifications, API documentation, and other materials provided by Provider for the Services, available at https://docs.cloudhealthoffice.com.

**"Confidential Information"** means all non-public information disclosed by one Party to the other Party that is marked as confidential or would reasonably be considered confidential given the nature of the information or circumstances of disclosure.

---

## 2. Scope of Service

### 2.1 EDI Transaction Processing

The Services include processing of the following HIPAA X12 EDI transactions:

- **837 Claims**: Professional (837P), Institutional (837I), Dental (837D)
- **270/271**: Eligibility Inquiry and Response
- **276/277**: Claim Status Inquiry and Response (including ValueAdds277 enhancements)
- **275**: Additional Information to Support a Health Care Claim or Encounter
- **278**: Health Care Services Review Information (Prior Authorization)
- **835**: Health Care Claim Payment/Remittance Advice
- **834**: Benefit Enrollment and Maintenance

### 2.2 FHIR R4 APIs

The Services include FHIR R4-compliant APIs in accordance with CMS-0057-F (CMS Interoperability and Patient Access Final Rule):

- **Patient Access API**: USCDI v1/v2 data classes, 5-year claim history
- **Provider Access API**: Provider directory, claim status, remittance advice
- **Prior Authorization API**: Da Vinci PAS 2.0 implementation (72-hour/7-day SLAs)
- **Payer-to-Payer API**: Member data exchange (5-year history)

All APIs conform to Da Vinci Implementation Guides: PDex, PAS, CRD, and DTR.

### 2.3 Platform Infrastructure

The Services are hosted on Microsoft Azure (United States regions) and include:

- **Multi-tenant SaaS architecture**: Isolated Logic Apps, Storage Accounts, Service Bus namespaces per customer
- **Data Lake Storage Gen2**: Hierarchical namespace for HIPAA-compliant EDI file archival (7-year retention)
- **Integration Account**: X12 schema management and trading partner configuration
- **Application Insights**: Real-time monitoring, telemetry, and alerting
- **Azure Key Vault**: HSM-backed encryption key management (Premium tier)

### 2.4 Service Level Agreement

Provider commits to the following availability targets:

| Subscription Tier | Monthly Uptime Commitment |
|-------------------|---------------------------|
| Starter | 99.5% |
| Professional | 99.9% |
| Enterprise | 99.95% |

Detailed SLA terms are documented in the [Service Level Agreement](./sla.md) incorporated by reference.

### 2.5 Support Response Times

| Subscription Tier | Critical | High | Medium | Low |
|-------------------|----------|------|--------|-----|
| Starter | 4 hours | 1 business day | 2 business days | 5 business days |
| Professional | 1 hour | 4 hours | 1 business day | 2 business days |
| Enterprise | 30 minutes | 1 hour | 4 hours | 1 business day |

**Support Channels:**
- Email: support@cloudhealthoffice.com (monitored 24/7)
- Support Portal: https://support.cloudhealthoffice.com
- Phone: 1-888-CLOUD-55 (Enterprise tier only)

---

## 3. Customer Obligations

### 3.1 Account Security

Customer is responsible for:

- Maintaining the confidentiality of all access credentials, API keys, and authentication tokens
- All activities that occur under Customer's account or by Customer's Authorized Users
- Immediately notifying Provider of any unauthorized access to Customer's account
- Implementing multi-factor authentication (MFA) for all Authorized Users with administrative privileges
- Restricting access to the Services on a need-to-know basis consistent with the principle of least privilege

### 3.2 Compliance with Laws

Customer represents and warrants that:

- Customer is a Covered Entity under HIPAA or qualifies as a Business Associate
- Customer will use the Services in compliance with all applicable federal, state, and local laws and regulations, including HIPAA, HITECH Act, state breach notification laws, and CMS regulations
- Customer has obtained all necessary consents and authorizations to submit data (including PHI) to the Services
- Customer Data does not violate any third-party intellectual property rights, privacy rights, or publicity rights

### 3.3 Integration and Configuration

Customer is responsible for:

- Providing accurate configuration information, including payer IDs, X12 qualifiers, SFTP credentials, and claims system API endpoints
- Maintaining the availability and security of Customer's claims systems, clearinghouses, and provider portals
- Notifying Provider within 2 business days of any changes to Customer's systems that may affect the Services (e.g., API endpoint changes, credential rotations, SFTP server migrations)
- Testing integration changes in a non-production environment before deploying to production
- Maintaining an active Microsoft Azure subscription if deploying the Services in Customer's own Azure tenant (self-hosted option)

### 3.4 Data Accuracy

Customer is solely responsible for:

- The accuracy, quality, and legality of Customer Data submitted to the Services
- Ensuring that PHI processed by the Services is accurate and complete
- Correcting any inaccuracies in Customer Data in a timely manner
- Validating EDI transaction data before submission to clearinghouses or trading partners

---

## 4. Fees and Payment

### 4.1 Subscription Fees

Customer shall pay Provider the subscription fees based on the selected tier:

| Tier | Monthly Price | Annual Price | Included Payers | Included Transactions/Month |
|------|---------------|--------------|-----------------|------------------------------|
| **Starter** | [Per Agreement] | [Per Agreement] | 1-3 | 10,000 |
| **Professional** | [Per Agreement] | [Per Agreement] | 4-10 | 100,000 |
| **Enterprise** | [Per Agreement] | [Per Agreement] | Unlimited | Unlimited |
| **Custom** | [Per Agreement] | [Per Agreement] | Unlimited | Unlimited |

Prices are exclusive of taxes. All amounts in USD.

### 4.2 Overage Charges

**Transaction Overages:**
- Transactions exceeding tier limits are billed at the per-transaction overage rate specified in the Order Form
- Overages are calculated monthly and invoiced in arrears
- Enterprise tier includes unlimited transactions (no overage charges)

**Storage Overages:**
- Each tier includes 1TB of Data Lake storage
- Additional storage beyond 1TB is billed at the storage overage rate specified in the Order Form (prorated daily) for all tiers
- Storage is calculated as average daily usage over the billing period

### 4.3 Payment Terms

- **Subscription fees** are due annually in advance, or monthly in advance if monthly billing is selected
- **Overage charges** are invoiced monthly in arrears within 5 business days of month-end
- **Payment is due within 30 days** of invoice date
- **Payment methods**: Credit card (Stripe), ACH transfer, wire transfer, or check
- **Auto-renewal**: Subscriptions automatically renew unless Customer provides 30-day advance written notice of non-renewal

### 4.4 Late Payment

- Fees not paid when due will accrue interest at the rate of **1.5% per month** (or the maximum rate permitted by law, whichever is lower)
- Provider may suspend access to the Services if payment is more than 15 days overdue, after providing 5 days' written notice
- Customer remains liable for all fees during suspension
- Provider may terminate the subscription if payment is more than 30 days overdue

### 4.5 Taxes

Fees are exclusive of all sales, use, value-added, excise, withholding, and other taxes, duties, and charges of any kind imposed by any federal, state, local, or foreign governmental entity. Customer is responsible for all such taxes, except for taxes based on Provider's net income.

### 4.6 Fee Increases

Provider may increase subscription fees upon **60 days' written notice**. Fee increases will take effect upon subscription renewal. If Customer does not agree to the fee increase, Customer may terminate the subscription by providing written notice before the renewal date.

---

## 5. Data Ownership and Security

### 5.1 Customer Data Ownership

Customer retains all right, title, and interest in and to Customer Data, including all PHI processed through the Services. Provider does not claim any ownership rights in Customer Data.

### 5.2 Business Associate Relationship

Provider is a Business Associate under HIPAA with respect to PHI processed on behalf of Customer. The Parties have executed a Business Associate Agreement ("BAA") that governs the use and disclosure of PHI. The BAA is incorporated by reference into these Terms of Service. In the event of any conflict between these Terms and the BAA, the BAA controls with respect to PHI.

### 5.3 Encryption

All Customer Data is protected by:

- **Encryption in transit**: TLS 1.3 (minimum TLS 1.2) for all data transmission
- **Encryption at rest**: AES-256 encryption for all stored data in Azure Storage, Service Bus, and databases
- **Key management**: Azure Key Vault Premium (HSM-backed) with customer-managed keys (CMK) available for Enterprise tier

### 5.4 Data Retention

**During Subscription:**
- EDI transaction files: 7 years (configurable from 1 to 10 years)
- FHIR resources: 5 years (CMS requirement)
- Application logs: 365 days
- Audit logs: 7 years

**After Termination:**
- Customer may export all Customer Data within 60 days following termination
- Provider will securely delete all Customer Data within 90 days following termination, except as required by law or to comply with the BAA
- Backup tapes and snapshots are purged according to standard retention schedules (maximum 90 days)
- De-identified, aggregated data may be retained for analytics and service improvement

### 5.5 Data Location

Customer Data is processed and stored in Microsoft Azure United States regions (East US, Central US, or West US) as selected by Customer during configuration. Data does not leave the selected Azure region except:

- As explicitly configured by Customer (e.g., geo-redundant storage)
- For support purposes with Customer's written consent
- As required by law with prior notice to Customer (if legally permitted)

### 5.6 Backup and Disaster Recovery

Provider maintains:

- **Geo-redundant storage (GRS)** for all EDI archives and FHIR data stores
- **Automated daily backups** with 30-day retention
- **Recovery Point Objective (RPO)**: 1 hour
- **Recovery Time Objective (RTO)**: 4 hours for Starter/Professional, 1 hour for Enterprise

---

## 6. Termination

### 6.1 Termination for Convenience

Either Party may terminate the subscription for any reason upon **30 days' written notice** to the other Party. Termination is effective at the end of the then-current Subscription Term.

### 6.2 Termination for Cause

Either Party may terminate the subscription immediately upon written notice if the other Party:

- Materially breaches these Terms and fails to cure the breach within **30 days** of written notice (or 10 days for payment breaches)
- Becomes insolvent, files for bankruptcy, or ceases to operate in the normal course of business
- Violates applicable laws or regulations, including HIPAA (immediate termination, no cure period)

### 6.3 Effect of Termination

Upon termination or expiration of the subscription:

- Customer's license to access and use the Services terminates immediately
- Customer shall pay all outstanding fees accrued through the termination date
- Customer may export Customer Data for **60 days** following termination via self-service export tools or by requesting assistance from Provider
- Provider will securely delete all Customer Data within **90 days** following termination, except as required by law or the BAA
- Provider will issue a certificate of data destruction upon Customer's request

### 6.4 No Refunds

Except as expressly provided in the SLA (Service Credits), fees paid are **non-refundable**. If Customer terminates for convenience before the end of the Subscription Term, Customer remains liable for all fees for the remainder of the term.

### 6.5 Survival

The following sections survive termination of these Terms: Section 5 (Data Ownership and Security - with respect to data destruction obligations), Section 6.3 (Effect of Termination), Section 6.4 (No Refunds), Section 7 (Limitation of Liability), Section 8 (Indemnification), Section 9 (Confidentiality), and Section 10 (Dispute Resolution).

---

## 7. Limitation of Liability

### 7.1 EXCLUSION OF CONSEQUENTIAL DAMAGES

EXCEPT FOR BREACHES OF SECTION 5 (DATA OWNERSHIP AND SECURITY) OR SECTION 9 (CONFIDENTIALITY), IN NO EVENT SHALL EITHER PARTY BE LIABLE FOR ANY INDIRECT, INCIDENTAL, SPECIAL, CONSEQUENTIAL, OR PUNITIVE DAMAGES, INCLUDING LOST PROFITS, REVENUE, DATA, OR BUSINESS OPPORTUNITIES, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGES.

### 7.2 LIABILITY CAP

EXCEPT FOR:
- BREACHES OF SECTION 5 (DATA OWNERSHIP AND SECURITY)
- BREACHES OF SECTION 9 (CONFIDENTIALITY)
- SECTION 8 (INDEMNIFICATION OBLIGATIONS)
- CUSTOMER'S PAYMENT OBLIGATIONS

EACH PARTY'S TOTAL CUMULATIVE LIABILITY ARISING OUT OF OR RELATED TO THESE TERMS SHALL NOT EXCEED THE TOTAL SUBSCRIPTION FEES PAID OR PAYABLE BY CUSTOMER IN THE **12 MONTHS PRECEDING THE EVENT** GIVING RISE TO LIABILITY.

### 7.3 Basis of the Bargain

THE LIMITATIONS IN THIS SECTION 7 REFLECT THE ALLOCATION OF RISK BETWEEN THE PARTIES AND ARE A FUNDAMENTAL BASIS OF THE BARGAIN. THE SERVICES WOULD NOT BE PROVIDED WITHOUT THESE LIMITATIONS.

### 7.4 DISCLAIMER OF WARRANTIES

EXCEPT AS EXPRESSLY SET FORTH IN THE SLA, THE SERVICES ARE PROVIDED "AS IS" AND "AS AVAILABLE." PROVIDER DISCLAIMS ALL WARRANTIES, EXPRESS OR IMPLIED, INCLUDING WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE, NON-INFRINGEMENT, AND TITLE.

PROVIDER DOES NOT WARRANT THAT THE SERVICES WILL BE UNINTERRUPTED, ERROR-FREE, COMPLETELY SECURE, OR FREE FROM VIRUSES OR OTHER HARMFUL COMPONENTS. CUSTOMER ACKNOWLEDGES THAT NO INTERNET OR CLOUD-BASED SERVICE IS 100% SECURE AND ACCEPTS THE RISKS INHERENT IN ELECTRONIC DATA TRANSMISSION.

---

## 8. Indemnification

### 8.1 Provider Indemnification

Provider shall defend, indemnify, and hold harmless Customer from and against any third-party claims, damages, losses, and expenses (including reasonable attorneys' fees) arising from:

- Allegations that the Services infringe a third party's U.S. patent, copyright, or trademark
- Provider's gross negligence or willful misconduct
- Provider's breach of Section 5 (Data Ownership and Security) or the BAA

**Exclusions**: Provider has no obligation to indemnify Customer for claims arising from:
- Modifications to the Services made by Customer or third parties not authorized by Provider
- Use of the Services in combination with third-party products not approved by Provider
- Customer's breach of these Terms
- Use of the Services in violation of applicable law

### 8.2 Customer Indemnification

Customer shall defend, indemnify, and hold harmless Provider from and against any third-party claims, damages, losses, and expenses (including reasonable attorneys' fees) arising from:

- Customer Data, including allegations of infringement, violation of privacy rights, or defamation
- Customer's breach of these Terms, including failure to comply with HIPAA or other applicable laws
- Customer's use of the Services in violation of these Terms or applicable law
- Claims by Customer's employees, contractors, or Authorized Users

### 8.3 Indemnification Procedures

The indemnified Party shall:

- Promptly notify the indemnifying Party in writing of any claim subject to indemnification (failure to promptly notify will not relieve the indemnifying Party except to the extent it is prejudiced by the delay)
- Grant sole control of the defense and settlement to the indemnifying Party (provided settlements do not admit liability on behalf of the indemnified Party or impose obligations on the indemnified Party without its consent)
- Provide reasonable cooperation in the defense, at the indemnifying Party's expense

---

## 9. Confidentiality

### 9.1 Confidential Information

Each Party agrees to maintain the confidentiality of the other Party's Confidential Information and use it only to perform its obligations under these Terms. Confidential Information includes:

**Provider's Confidential Information:**
- Source code, architecture diagrams, trade secrets, and proprietary algorithms
- Security controls, vulnerability assessments, and penetration test results
- Pricing information (except Customer's own pricing)
- Roadmap and unreleased features

**Customer's Confidential Information:**
- Customer Data (including PHI)
- Business strategies, financial information, and patient/member statistics
- Configuration details, integration specifications, and trading partner information
- The terms of this Agreement (except as required by law)

### 9.2 Exceptions

Confidential Information does not include information that:

- Is or becomes publicly available through no fault of the receiving Party
- Was rightfully known to the receiving Party prior to disclosure
- Is independently developed by the receiving Party without use of the Confidential Information
- Is rightfully obtained from a third party without breach of confidentiality obligations

### 9.3 Required Disclosure

The receiving Party may disclose Confidential Information if required by law, court order, or government regulation, provided the receiving Party:

- Gives the disclosing Party prompt written notice (if legally permitted)
- Cooperates with the disclosing Party's efforts to seek a protective order or other appropriate relief
- Discloses only the minimum Confidential Information required

### 9.4 Term

The obligations in this Section 9 survive termination of these Terms for **5 years**, except that obligations with respect to PHI survive until the PHI is returned or destroyed as required by the BAA.

---

## 10. Dispute Resolution

### 10.1 Informal Resolution

Before initiating formal dispute resolution, the Parties agree to attempt in good faith to resolve any dispute through informal negotiation for **30 days**. Either Party may initiate informal resolution by sending written notice to the other Party's designated representative.

### 10.2 Mediation

If informal negotiation fails to resolve the dispute within 30 days, either Party may request non-binding mediation administered by **JAMS** (Judicial Arbitration and Mediation Services) in accordance with its Mediation Rules. The Parties will equally share the costs of mediation. Mediation shall take place in **Wilmington, Delaware** (or remotely by mutual agreement).

### 10.3 Binding Arbitration

If mediation fails to resolve the dispute within 60 days of the mediation request, either Party may initiate binding arbitration. Arbitration shall be:

- Administered by **JAMS** in accordance with its Streamlined Arbitration Rules & Procedures
- Conducted by a single arbitrator mutually selected by the Parties (or appointed by JAMS if no agreement within 14 days)
- Held in **Wilmington, Delaware** (or remotely by mutual agreement)
- Conducted in English
- Subject to the Federal Arbitration Act (9 U.S.C. §§ 1-16)

The arbitrator's award shall be final and binding, and judgment on the award may be entered in any court of competent jurisdiction. Each Party shall bear its own attorneys' fees and costs, except the arbitrator may award reasonable attorneys' fees and costs to the prevailing Party.

### 10.4 Exceptions to Arbitration

Either Party may seek injunctive or equitable relief in a court of competent jurisdiction without first engaging in arbitration if:

- The dispute involves alleged infringement of intellectual property rights
- Immediate injunctive relief is necessary to prevent irreparable harm

### 10.5 Class Action Waiver

EACH PARTY AGREES THAT ANY DISPUTE RESOLUTION PROCEEDINGS WILL BE CONDUCTED ONLY ON AN INDIVIDUAL BASIS AND NOT IN A CLASS, CONSOLIDATED, OR REPRESENTATIVE ACTION. Neither Party may bring claims against the other as a class member in any class or representative action.

### 10.6 Governing Law

These Terms are governed by the laws of the **State of Delaware**, United States, without regard to its conflicts of law principles. The United Nations Convention on Contracts for the International Sale of Goods does not apply.

---

## 11. Miscellaneous

### 11.1 Entire Agreement

These Terms, together with the BAA, SLA, Privacy Policy, and any applicable Order Form, constitute the entire agreement between the Parties and supersede all prior or contemporaneous agreements, understandings, and communications, whether written or oral, relating to the subject matter hereof.

### 11.2 Amendments

Provider may update these Terms from time to time by posting a new version on the Cloud Health Office website. Material changes will be communicated to Customer via email at least **30 days** before the effective date. Customer's continued use of the Services after the effective date constitutes acceptance of the updated Terms. If Customer does not agree to the updated Terms, Customer may terminate the subscription as provided in Section 6.1.

### 11.3 Waiver

No waiver of any provision of these Terms is effective unless in writing and signed by the waiving Party. No waiver of a breach constitutes a waiver of any other breach.

### 11.4 Assignment

Customer may not assign these Terms or any rights hereunder without Provider's prior written consent (not to be unreasonably withheld). Provider may assign these Terms without Customer's consent:

- To an affiliate or subsidiary
- In connection with a merger, acquisition, reorganization, or sale of all or substantially all of Provider's assets

Any attempted assignment in violation of this Section is void. These Terms bind and inure to the benefit of the Parties and their permitted successors and assigns.

### 11.5 Severability

If any provision of these Terms is held invalid, illegal, or unenforceable by a court of competent jurisdiction, the remaining provisions remain in full force and effect, and the invalid provision shall be modified to the minimum extent necessary to make it valid and enforceable.

### 11.6 Force Majeure

Neither Party is liable for delays or failures in performance due to causes beyond its reasonable control, including acts of God, natural disasters, war, terrorism, riots, labor disputes, government actions, pandemics, or failures of Internet infrastructure or third-party services. The affected Party shall promptly notify the other Party and use commercially reasonable efforts to mitigate the impact. If force majeure continues for more than 60 days, either Party may terminate the subscription upon written notice.

### 11.7 Notices

All notices under these Terms must be in writing and delivered via:

- Email with read receipt (effective upon receipt)
- Certified mail, return receipt requested (effective 5 days after mailing)
- Overnight courier (effective upon delivery)

**Notices to Provider:**  
Cloud Health Office  
Attn: Legal Department  
Email: legal@cloudhealthoffice.com  
Address: [Company Address]

**Notices to Customer:**  
To the administrative email address provided during account registration.

Customer is responsible for maintaining current contact information in the account settings.

### 11.8 Independent Contractors

The Parties are independent contractors. Nothing in these Terms creates an employment, agency, partnership, joint venture, or franchise relationship. Neither Party has authority to bind or commit the other Party.

### 11.9 Export Compliance

Customer acknowledges the Services may be subject to U.S. export control laws, including the Export Administration Regulations (EAR) and sanctions administered by the Office of Foreign Assets Control (OFAC). Customer agrees to comply with all applicable export laws and not to export, re-export, or transfer the Services to prohibited countries, entities, or individuals.

### 11.10 Government End Users

If Customer is a U.S. federal, state, or local government entity, the Services are "commercial computer software" and "commercial computer software documentation" as defined in FAR 12.212 and DFARS 227.7202. Government use is subject to these Terms in accordance with FAR 12.212 and DFARS 227.7202-3.

### 11.11 Publicity

Provider may identify Customer as a customer of Cloud Health Office in marketing materials, including on the Provider website and in case studies, subject to Customer's prior approval of any specific use case or customer story. Customer may revoke consent by providing written notice to Provider.

### 11.12 Third-Party Services

The Services may integrate with or facilitate access to third-party services (e.g., clearinghouses, claims systems, Azure services). Provider is not responsible for third-party services, and Customer's use of third-party services is subject to separate terms and conditions with those third parties.

### 11.13 Counterparts

These Terms (if executed as a separate agreement) may be executed in counterparts, including electronic or scanned copies, each of which is deemed an original and all of which constitute one agreement.

### 11.14 English Language

These Terms are written in English. Any translation is provided for convenience only. In the event of conflict between the English version and a translation, the English version controls.

---

## 12. Acceptance

By clicking "I Agree," creating an account, accessing the Services, or paying subscription fees, Customer agrees to be bound by these Terms of Service.

**Last Updated**: February 17, 2026

---

## Contact Information

**Cloud Health Office**  
Website: https://cloudhealthoffice.com  
Support: support@cloudhealthoffice.com  
Sales: sales@cloudhealthoffice.com  
Legal: legal@cloudhealthoffice.com

---

**Cloud Health Office** – The Inevitable Evolution of Healthcare EDI  
**Source-Available (BSL 1.1) | Azure-Native | HIPAA-Compliant | CMS-0057-F Ready**

© 2026 Aurelianware. All rights reserved.
