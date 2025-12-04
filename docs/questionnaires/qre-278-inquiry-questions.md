# QRE 278 Inquiry Questionnaire (Questions Only)

This document captures the prompts from the "X12 278 Healthcare Services Review — Inquiry and Response (005010X215) Implementation Requirements Questionnaire" without answers, sample responses, or identifying information.

## Contact Information
- Provide the trading partner technical contact name, phone, and email.
- Provide the Availity technical contact name, phone, and email.
- Provide the trading partner account manager name, phone, and email.
- Provide the Availity account manager name, phone, and email.
- Provide the trading partner escalation contact name, phone, and email.
- Provide the Availity escalation contact name, phone, and email.
- List any additional trading partner contacts with name, phone, and email.
- List any additional Availity contacts with name, phone, and email.

## Payer Identification and Branding
- Identify the payer ID to display in Availity Essentials and note any line-of-business variations.
- Confirm whether Availity may publish the payer ID in the Availity Health Plan Partners document and, if not, whether this is a controlled deployment.
- Specify the payer or plan name that should appear in Availity Essentials and be transmitted in loop 2010A NM103.

## Implementation States and Logo
- State whether the implementation is nationwide; if not, list the states where the payer ID should appear.
- Supply the payer logo file (GIF, PNG, or JPG) for display in Availity Essentials.

## Connectivity
- Indicate whether an XML envelope is required for X215 messages and describe wrapper expectations when applicable.
- Share any alternative requirements if an XML wrapper is not needed.
- Provide the test URL(s) and user ID(s) (with passwords sent separately) for each region or state as applicable.
- Provide the production URL(s) and user ID(s) (with passwords sent separately) for each region or state as applicable.
- Describe system hours of availability.
- Specify how many concurrent threads the system supports.

## Enveloping Requirements
- Supply the sender and receiver identifiers (ISA06, ISA08, GS02, GS03) expected in request and response envelopes.
- Provide the values for 2010A NM103 and NM109 used in both directions.
- Share a formatted ISA/GS control segment example if available.

## Payer Enhancements and Standards
- Confirm whether uppercase characters are acceptable.
- Confirm whether spaces from the basic X12 character set are accepted.
- Confirm whether characters from the X12 extended character set are accepted.

## Testing
- Indicate whether test scenarios with members, providers, and related data can be supplied.
- Note any minimum or maximum number of test transactions that will be accepted.
- Provide the date or date range when the payer will be ready to receive a test file.

## Essentials Screen Fields
- Document any payer-specific rules for Member ID entry, including scenarios where the Authorization Number substitutes for Member ID.
- Document requirements for patient relationship, first name, last name, and date of birth when the patient is not the subscriber.
- Specify whether patient gender must be provided and, if so, the acceptable values.
- Describe any requirements for Requesting Provider information, including entity type selection, name fields, NPI, address, and contact details.
- Outline acceptable values or constraints for service information fields such as date qualifier, date formats, allowable date ranges, and Authorization or Referral Number usage.

## Additional Submission Guidance
- Provide any banner messages or notices that must display to providers during the inquiry workflow, including placement details.
- Identify any attestations providers must complete before submission and where they should appear.
- List any payer-specific error messages or AAA mappings that must be returned to providers, including the associated conditions.
