# QRE 275 Medical Attachments Questionnaire (Questions Only)

This document captures the prompts from the "X12 275: Medical Attachments Implementation Requirements Questionnaire" without
answers, sample responses, or identifying information.

## Contact Information
- Trading partner technical contact: name, phone, and email
- Availity technical contact: name, phone, and email
- Trading partner account manager: name, phone, and email
- Availity account manager: name, phone, and email
- Trading partner escalation contact: name, phone, and email
- Availity escalation contact: name, phone, and email
- Additional trading partner contacts: name, phone, and email
- Other Availity contacts: name, phone, and email

## Mode and Transaction Type
- Which attachment exchange modes will you support? (Batch/EDI and/or Web Portal)
- Which medical attachment transaction types are you implementing? (Claim, Authorization, 277 RFAI)
- For each transaction type, which TR3 version will you implement?
- Which response file formats will you generate? (999 5010, 824 5010, 999 6020, 824 6020)

## Payer Identification and Branding
1. What payer ID should appear in Availity Essentials?
2. Do you authorize Availity to publish your payer ID(s) in the Availity Health Plan Partners document?
3. If publication is not authorized, is this a controlled deployment to a limited provider group?
4. What payer or plan name should appear in Essentials menus?
5. Is the implementation nationwide across all states? If not, which states should display the payer ID?
6. Provide the payer logo file (GIF, PNG, or JPG) for display in Availity Essentials.

## Attachment Transaction Types
- Will you accept solicited attachments, unsolicited attachments, or both?

## Attachment Types, Sizes, and Volume
1. What is the maximum attachment file size your systems can accept?
2. What is the anticipated monthly attachment volume?
3. Do you require a MIME header in the 275 file?
4. Which attachment file types can you receive? (PDF, GIF, TXT, JSON, JPG, TIFF, XML, CSV, PNG, Microsoft Office formats, etc.)
5. Do you restrict the maximum number of characters in attachment file names? If yes, what is the limit (including extension)?

## Product Integration
- Which Availity products will integrate with attachments? (Authorizations, Direct Data Entry, Claim Status Inquiry, Overpayments, Appeals)
- For Authorizations:
  - Will attachments be submitted during authorization submission?
  - Will attachments be submitted after authorization submission?
  - Do you support Report Type Code and LOINC code mapping for the authorization solution?
  - Are edits required to relax the STC option in unsolicited 275 transactions?

## Appeals (Supplemental Questionnaire noted)
1. Will you allow more than one file with the same name and extension on a single appeal?
2. Do you impose limits on the number or size of attachments per appeal? If yes, specify the limits.

## File Retrieval and Connectivity
- Preferred production mailbox pattern (Push/Pull, Pull/Push, Push/Push). For each option you support, provide:
  - URL or endpoint details
  - Port numbers
  - Username credentials
  - Password delivery method
  - Drop location(s) for each attachment type (Claim, Authorization, Appeal)
  - Retrieval location(s) for response files
- Do you support concurrent FTP logins? If not, describe login requirements per attachment type.
- QA environment connectivity details matching the production information above.

## File Naming Convention
1. Can you accept the Availity naming standard `MMDDYYYYHHMMSSsssSequenceNumber.275`?
2. If not, what file naming convention is required?

## Testing
1. What is the target date to begin user acceptance testing (UAT)?
2. Will the test environment remain available after production approval?
3. Do you have specific testing requirements? If yes, describe them.
4. Do you need Availity to generate sample 275 files? If yes, indicate attachment type and single vs. multi-attachment needs.
5. How many test files will you accept during testing?
6. Must test files contain valid provider data?
7. Must test files contain valid membership records?
8. Do you have a designated payer ID for testing? If yes, provide it.
9. Do you require a minimum or maximum number of test transactions? If yes, specify the thresholds.
10. When will you be ready to receive a test file (date or date range)?
11. Is Secure FTP over the internet acceptable for testing and production connectivity? If not, describe your requirements.

## Production
- What is your target go-live date for production processing of this transaction?
