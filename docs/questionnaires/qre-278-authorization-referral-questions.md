# QRE 278 Authorization and Referral Questionnaire (Questions Only)

This document captures the prompts from the "X12 278 Healthcare Services Review — Request for Review and Response (005010X217) Implementation Requirements Questionnaire" without answers, sample responses, or identifying information.

## Contact Information and Milestones
- Provide trading partner and Availity contacts for technical, account management, escalation, and any additional stakeholders (name, phone, email for each).
- Supply completion dates for the first-30-day deliverables (payer name/ID/states, testing process, required connectivity, test connectivity, production connectivity, portal workflow and APIs).
- Supply completion dates for the first-60-day deliverables (enveloping requirements, payer logo, Availity request standards, Availity response standards, Essentials submission fields, additional submission information, submission response details).

## Payer Identification and Implementation Footprint
- Identify the payer ID to display in Availity Essentials and note any alternate IDs by line of business.
- Confirm whether Availity may publish the payer ID in the Availity Health Plan Partners document and, if not, whether this is a controlled deployment limited to selected providers.
- Specify the payer or plan name that should appear in Availity Essentials and in loop 2010A NM103.
- State whether the implementation is nationwide; if not, list the states in which the payer ID should be available.
- Provide the payer logo file (GIF, PNG, or JPG) for use in Availity Essentials.

## Testing Process
- Describe the production approval process.
- Confirm whether the test environment remains available after production approval and describe expectations.
- List any specific testing requirements or exclusions that apply to this integration.
- Indicate whether test cases must include valid provider data, and whether membership records must be valid.
- Provide any designated payer ID(s) for use during testing, including line-of-business variations if applicable.
- State the minimum or maximum number of test transactions the payer will accept.
- Provide the date or date range when the payer will be ready to receive test transactions.

## Connectivity and Integration
- Identify utilization management vendors and tools involved in processing authorization or referral transactions.
- Confirm whether the existing EDI gateway or UM system supports X12 278 transactions and provide the companion guide if available.
- If X12 278 is not supported today, list the EDI gateway vendor details and any expected integration impacts.
- Indicate whether an XML wrapper is required for X12 278 exchanges and supply the envelope specification when applicable.
- Describe any alternative requirements if an XML wrapper is not required.
- Confirm whether HTTPS connectivity requirements vary by region or state and provide region-specific details when they do.

## Test and Production Connectivity
- Provide the test endpoint URL(s) for each API, including the X12 278 endpoint, along with user ID(s); deliver password information separately.
- Provide the production endpoint URL(s) for each API, including the X12 278 endpoint, along with user ID(s); deliver password information separately.
- Specify system hours of availability for production processing.
- State the maximum number of concurrent threads or sessions the payer’s platform supports.

## Portal Workflow and Supporting APIs
- Confirm whether real-time X12 270/271 transactions are supported for eligibility checks used by the authorization/referral workflow.
- Indicate whether the payer is part of the Blue Cross Association and therefore requires Electronic Provider Access (EPA) API integration.

## Value-Add Features and Optional APIs
- State whether Certification Type Codes for cancel and revision actions are supported via X12 278 and describe any status restrictions.
- Describe whether authorization revisions or cancellations are permitted at all statuses or limited to pended cases.
- Confirm whether Is Auth Required rules are centralized and accessible via real-time API, and provide current capabilities.
- Confirm whether a Provider Search API can be supported, whether a unique provider or facility identifier is required, and whether facilities are included in the dataset.
- Identify third-party benefit managers used for authorizations and confirm their readiness for integrated routing or single sign-on workflows.
- Indicate whether real-time X12 275 attachments are currently supported or planned, whether documentation is required at submission, and whether unsolicited attachments can be accepted post-submission.
- Identify any medical necessity questionnaire vendors (such as MCG or InterQual) that must integrate with the workflow and outline expectations.
- Confirm whether Epic Payer Platform integration is required for either authorization or attachment routing.

## Enveloping Requirements and Standards
- Provide sender and receiver identifiers for ISA06, ISA08, GS02, and GS03 in both request and response envelopes.
- Supply the values for loop 2010A NM103 and NM109 in both directions.
- Share a formatted ISA/GS control-segment example if available.
- Confirm acceptance of uppercase characters, spaces in the X12 basic character set, and characters from the X12 extended character set.

## Essentials Submission Fields
- Provide payer-specific rules for member demographics (Member ID, relationship, patient name, date of birth, gender) including when fields become optional.
- Document requirements for the requesting provider or facility (entity type selection, name fields, NPI, payer-assigned identifiers, specialty/taxonomy, address, contact details).
- Describe expectations for request-level information (type of request, certification type code, service type codes, place of service, quantity/type values, date range limits, admission and discharge rules, diagnosis coding allowances, message usage).
- Specify requirements for procedure-level data (CPT/HCPCS/Revenue codes, date ranges, quantity types and limits) when authorizations are submitted.
- Outline requirements for rendering providers, rendering facilities, and referred-to providers, including acceptable provider roles, identifiers, and contact information.
- Define requirements for referral service event dates (date qualifiers, formats, and valid date ranges).

## Additional Submission Guidance
- Identify banner messages or notices that must appear in the provider workflow and where each message should display.
- Provide any required provider attestations and where they should appear within the workflow.

## Submission Response Handling
- Describe the error messages or AAA code mappings the payer expects to return to providers, including any custom messaging requirements.
