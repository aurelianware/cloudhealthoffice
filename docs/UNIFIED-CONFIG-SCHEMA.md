# Unified Availity Integration Configuration Schema

## Table of Contents

- [Overview](#overview)
- [Purpose](#purpose)
- [Schema Structure](#schema-structure)
- [Complete Field Reference](#complete-field-reference)
  - [Root Properties](#root-properties)
  - [Contacts Object](#contacts-object)
  - [Geography Object](#geography-object)
  - [Common Configuration](#common-configuration)
  - [Module Configurations](#module-configurations)
- [Module-by-Module Configuration Guide](#module-by-module-configuration-guide)
- [Validation Rules and Constraints](#validation-rules-and-constraints)
- [Example Configurations](#example-configurations)
- [How to Extend the Schema](#how-to-extend-the-schema)
- [Migration Guide](#migration-guide)

---

## Overview

The **Unified Availity Integration Configuration Schema** provides a comprehensive, configuration-driven approach to onboarding health plans to the Availity integration platform. This schema supports all major Availity transaction types without requiring custom coding per payer.

### Supported Transaction Types

- **837 Claims** - Professional, Institutional, and Dental claims submission
- **270/271 Eligibility** - Real-time eligibility verification
- **276/277 Claim Status** - Claim status inquiries and responses
- **Appeals** - Appeals submission and tracking
- **275 Attachments** - Clinical and administrative attachments
- **ECS (Enhanced Claim Status)** - Advanced claim status with extended data

### Key Benefits

- **Configuration-Driven**: No custom code required for new payer onboarding
- **Standardized**: Consistent structure across all payers
- **Flexible**: Supports multiple transaction modes (real-time web, B2B, EDI batch)
- **Validated**: Built-in validation ensures configuration correctness
- **Extensible**: Easy to add new modules and features

---

## Purpose

This schema enables:

1. **Rapid Payer Onboarding** - Add new payers by creating a configuration file
2. **Self-Service Configuration** - Payers can configure their own integration parameters
3. **Centralized Management** - Single source of truth for all payer configurations
4. **Automated Validation** - Ensure configurations are correct before deployment
5. **Version Control** - Track configuration changes over time
6. **Multi-Environment Support** - Separate test and production configurations

---

## Schema Structure

The schema is organized hierarchically:

```
Root Configuration
├── Organization Information (name, IDs, Sentinel logo)
├── Contacts (technical, account manager, escalation)
├── Geography (nationwide or state-specific)
├── Common Configuration
│   ├── Enveloping (ISA/GS segments)
│   ├── Connectivity (HTTPS/FTP settings)
│   ├── Testing (certification requirements)
│   ├── Character Set (encoding rules)
│   └── Error Handling (acknowledgments)
└── Modules
    ├── Claims 837
    ├── Eligibility 270/271
    ├── Claim Status 276/277
    ├── Appeals
    ├── Attachments 275
    └── ECS
```

---

## Complete Field Reference

### Root Properties

#### `organizationName` (string, required)
**Description**: Health plan organization name  
**Constraints**: 1-100 characters  
**Example**: `"Blue Cross Blue Shield of Texas"`

#### `payerId` (string, required)
**Description**: Primary payer ID for Availity routing  
**Pattern**: `^[A-Z0-9]+$`  
**Constraints**: 5-20 characters  
**Example**: `"66917"`, `"030240928"`

#### `payerName` (string, required)
**Description**: Display name for provider portals  
**Constraints**: 1-100 characters  
**Example**: `"{config.payerName}"`, `"Health Plan QNXT"`

#### `logo` (string, uri format, optional)
**Description**: URL to payer logo  
**Requirements**: 800x400px PNG/SVG recommended (Cloud Health Office Sentinel branding available at docs/images/logo-cloudhealthoffice-sentinel-primary.png.svg)  
**Example**: `"https://example.com/logo.png"` or `"docs/images/logo-cloudhealthoffice-sentinel-primary.png.svg"`

#### `tradingPartnerId` (string, optional)
**Description**: Trading partner ID for connectivity  
**Pattern**: `^[A-Z0-9-]+$`  
**Constraints**: 1-50 characters  
**Example**: `"TP12345"`

#### `controlledDeployment` (boolean, optional)
**Description**: Whether deployment is restricted to specific providers  
**Default**: `false`  
**Use Case**: Use `true` for phased rollouts or pilot programs

---

### Contacts Object

The `contacts` object is **required** and must contain at least three contact types.

#### Contact Structure

Each contact has the following properties:

- **`name`** (string, required): Contact person's full name (1-100 characters)
- **`email`** (string, email format, required): Contact email address
- **`phone`** (string, optional): 10-digit phone number (no formatting, e.g., `"5551234567"`)

#### Required Contact Types

1. **`technical`** - Technical contact for integration issues
2. **`accountManager`** - Account manager contact
3. **`escalation`** - Escalation contact for critical issues

#### Optional Contact Types

4. **`additional`** - Additional contact (optional)

**Example**:
```json
{
  "contacts": {
    "technical": {
      "name": "John Smith",
      "email": "john.smith@example.com",
      "phone": "5551234567"
    },
    "accountManager": {
      "name": "Jane Doe",
      "email": "jane.doe@example.com",
      "phone": "5551234568"
    },
    "escalation": {
      "name": "Bob Johnson",
      "email": "bob.johnson@example.com",
      "phone": "5551234569"
    }
  }
}
```

---

### Geography Object

Defines geographic coverage for the payer.

#### `nationwide` (boolean)
**Description**: Whether the payer operates nationwide  
**Default**: `false`

#### `states` (array of strings)
**Description**: Array of 2-letter US state codes  
**Pattern**: `^[A-Z]{2}$`  
**Constraints**: 1-56 items (required if `nationwide=false`)  
**Valid Codes**: AL, AK, AZ, AR, CA, CO, CT, DE, FL, GA, HI, ID, IL, IN, IA, KS, KY, LA, ME, MD, MA, MI, MN, MS, MO, MT, NE, NV, NH, NJ, NM, NY, NC, ND, OH, OK, OR, PA, RI, SC, SD, TN, TX, UT, VT, VA, WA, WV, WI, WY, DC, PR, VI, GU, AS, MP

**Validation Rule**: If `nationwide=true`, `states` must not be present. If `nationwide=false`, `states` is required.

**Examples**:
```json
{
  "geography": {
    "nationwide": true
  }
}
```

```json
{
  "geography": {
    "nationwide": false,
    "states": ["TX", "OK", "LA", "AR", "NM"]
  }
}
```

---

### Common Configuration

Configuration that applies across multiple modules.

#### Enveloping Object

ISA/GS segment values for X12 EDI enveloping.

- **`isaAuthorizationQualifier`** (enum): ISA01 - Authorization Information Qualifier
  - Values: `"00"` (No Authorization), `"03"` (Password)
  - Default: `"00"`

- **`isaSecurityQualifier`** (enum): ISA03 - Security Information Qualifier
  - Values: `"00"` (No Security), `"01"` (Password)
  - Default: `"00"`

- **`isaInterchangeIdQualifier`** (enum): ISA05/ISA07 - Interchange ID Qualifier
  - Values: `"01"` (DUNS), `"14"` (Telephone), `"20"` (UCC), `"27"` (Carrier ID), `"28"` (Fiscal ID), `"29"` (DEA), `"30"` (State License), `"33"` (NCPDP), `"ZZ"` (Mutually Defined)
  - Default: `"ZZ"`

- **`isaUsageIndicator`** (enum): ISA15 - Usage Indicator
  - Values: `"P"` (Production), `"T"` (Test)
  - Default: `"P"`

- **`gsApplicationSenderCode`** (string): GS02 - Application Sender's Code (2-15 characters)
- **`gsApplicationReceiverCode`** (string): GS03 - Application Receiver's Code (2-15 characters)
- **`gsVersionCode`** (enum): GS08 - Version/Release/Industry Identifier Code
  - Values: `"005010"`, `"005010X212"`, `"005010X217"`, `"005010X221"`, `"005010X222"`
  - Default: `"005010"`

#### Connectivity Object

HTTPS/FTP connectivity configuration.

- **`protocol`** (enum): Connection protocol
  - Values: `"HTTPS"`, `"SFTP"`, `"FTP"`
  - Default: `"HTTPS"`

- **`timeout`** (integer): Connection timeout in seconds
  - Range: 10-300
  - Default: 30

- **`retryAttempts`** (integer): Number of retry attempts on failure
  - Range: 0-5
  - Default: 3

- **`maxThreads`** (integer): Maximum number of concurrent threads
  - Range: 1-50
  - Default: 10

- **`keepAlive`** (boolean): Whether to maintain persistent connections
  - Default: `true`

#### Testing Object

Testing environment and certification requirements.

- **`testEnvironmentAvailable`** (boolean): Whether a test environment is available
  - Default: `true`

- **`certificationRequired`** (boolean): Whether certification is required before production
  - Default: `true`

- **`approvalProcess`** (string): Description of the approval process (max 500 characters)

- **`testDataProvided`** (boolean): Whether test data is provided by payer
  - Default: `false`

- **`certificationTimeline`** (string): Expected timeline for certification
  - Examples: `"2-4 weeks"`, `"1-2 months"`

#### Character Set Object

Character set and encoding requirements.

- **`uppercaseOnly`** (boolean): Whether only uppercase characters are allowed
  - Default: `false`

- **`spacesAllowed`** (boolean): Whether spaces are allowed in fields
  - Default: `true`

- **`extendedCharactersSupported`** (boolean): Whether extended ASCII characters are supported
  - Default: `false`

- **`encoding`** (enum): Character encoding
  - Values: `"ASCII"`, `"UTF-8"`, `"ISO-8859-1"`
  - Default: `"ASCII"`

#### Error Handling Object

Error handling and acknowledgment configuration.

- **`ta1Required`** (boolean): Whether TA1 acknowledgments are required
  - Default: `false`

- **`functional997Required`** (boolean): Whether 997 functional acknowledgments are required
  - Default: `true`

- **`functional999Required`** (boolean): Whether 999 implementation acknowledgments are required
  - Default: `false`

- **`unusedSegmentsAllowed`** (boolean): Whether unused segments are allowed in transactions
  - Default: `true`

- **`errorNotificationEmail`** (string, email format): Email address for error notifications

---

### Module Configurations

Each module can be independently enabled and configured.

#### Transaction Modes

All modules support multiple transaction modes:

1. **`realtime_web`** - Real-time web-based transactions
2. **`realtime_b2b`** - Real-time B2B (business-to-business) transactions
3. **`edi_batch`** - EDI batch file processing

Each transaction mode has:

- **`enabled`** (boolean): Whether this mode is enabled
- **`testUrl`** (uri): Test environment URL
- **`prodUrl`** (uri): Production environment URL
- **`ftpConfig`** (object): FTP configuration (for batch modes only)
  - `host` (string): FTP server hostname
  - `port` (integer, 1-65535): FTP server port (default: 22)
  - `username` (string): FTP username
  - `inboundFolder` (string): Folder for inbound files
  - `outboundFolder` (string): Folder for outbound files
  - `archiveFolder` (string): Folder for archived files
- **`timeout`** (integer, 10-300): Request timeout in seconds (default: 30)
- **`systemAvailability`** (string): System availability schedule
  - Examples: `"24/7"`, `"Monday-Friday 8AM-6PM EST"`
- **`continuousThreads`** (integer, 1-50): Number of continuous processing threads (default: 5)

---

## Module-by-Module Configuration Guide

### Appeals Module

**Configuration Key**: `modules.appeals`

#### Core Settings

- **`enabled`** (boolean, required): Enable appeals module
- **`transactionModes`**: Real-time web and B2B only (no batch mode)

#### Appeal-Specific Configuration

- **`requestReasons`** (array, required): Supported appeal request reasons
  - Constraints: 1-10 items
  - Examples: `"Medical Necessity"`, `"Timely Filing"`, `"Coding Error"`

- **`subStatuses`** (array): Supported appeal sub-statuses
  - Valid values: `"Denied"`, `"Partial Approval"`, `"Overturned"`, `"Upheld"`, `"In Progress"`, `"Additional Information Needed"`, `"Withdrawn"`, `"Closed"`

- **`decisions`** (array): Supported appeal decisions with reasons
  - Each decision has:
    - `decision` (enum): `"Approved"`, `"Denied"`, `"Partially Approved"`, `"Withdrawn"`
    - `decisionReasons` (array of strings): Reasons for the decision

- **`attachmentPattern`** (enum): When attachments can be submitted
  - Values: `"pre-appeal"` (before decision), `"post-appeal"` (after decision)
  - Default: `"pre-appeal"`

- **`additionalAttachmentsWindow`** (integer): Days allowed for additional attachments
  - Range: 0-90
  - Default: 10

- **`maxFileSize`** (integer): Maximum file size in bytes
  - Range: 1MB (1048576) - 100MB (104857600)
  - Default: 64MB (67108864)

- **`maxFilesPerAppeal`** (integer): Maximum number of files per appeal
  - Range: 1-50
  - Default: 10

- **`allowedFileTypes`** (array): Allowed file extensions
  - Valid values: `".gif"`, `".jpg"`, `".jpeg"`, `".pdf"`, `".tif"`, `".tiff"`, `".png"`, `".doc"`, `".docx"`
  - Default: `[".gif", ".jpg", ".jpeg", ".pdf", ".tif", ".tiff"]`

#### Contact and Provider Information

- **`contactInfoRequired`** (object): Required contact information fields
  - `phone` (boolean, default: false)
  - `fax` (boolean, default: false)
  - `email` (boolean, default: false)

- **`submittingProviderTypeRequired`** (boolean): Whether submitting provider type is required
  - Default: `false`

- **`providerAddressRequired`** (boolean): Whether provider address is required
  - Default: `false`

#### Additional Claims Support

- **`additionalClaimsSupported`** (boolean): Whether additional claims can be included
  - Default: `false`

- **`maxAdditionalClaims`** (integer): Maximum number of additional claims
  - Range: 0-25
  - Default: 0
  - **Business Rule**: Must be > 0 if `additionalClaimsSupported` is `true`

- **`multiClaimVerification`** (boolean): Whether multi-claim verification is enabled
  - Default: `false`

#### Advanced Features

- **`submitOnBehalfOfMember`** (boolean): Allow submission on behalf of member
  - Default: `false`

- **`documentDownloadSupported`** (boolean): Support document download
  - Default: `false`

- **`ecsIntegrationEnabled`** (boolean): Enable ECS integration for appeals
  - Default: `false`

---

### Claims 837 Module

**Configuration Key**: `modules.claims837`

#### Core Settings

- **`enabled`** (boolean, required): Enable Claims 837 module
- **`transactionModes`**: All modes supported (real-time web, B2B, EDI batch)

#### Claim Types

- **`claimTypes`** (object): Supported claim types
  - `professional` (boolean): 837P - Professional claims
  - `institutional` (boolean): 837I - Institutional claims
  - `dental` (boolean): 837D - Dental claims
  - `encounters` (boolean): Encounter data
  - **Business Rule**: At least one claim type must be enabled

#### Secondary and Tertiary Claims

- **`secondaryClaims`** (boolean): Support secondary claims
  - Default: `false`

- **`tertiaryClaims`** (boolean): Support tertiary claims
  - Default: `false`

#### File Structure

- **`fileStructure`** (object): File structure requirements
  - `maxClaimsPerFile` (integer, 1-10000): Maximum claims per file (default: 1000)
  - `maxClaimsPerTransaction` (integer, 1-100): Maximum claims per transaction set (default: 1)
  - `aggregation` (enum): Claim aggregation strategy
    - Values: `"single"`, `"batch"`
    - Default: `"single"`

#### File Naming

- **`fileNaming`** (object): File naming conventions
  - `professional` (string): Pattern for professional claim files
  - `institutional` (string): Pattern for institutional claim files
  - `dental` (string): Pattern for dental claim files
  - Use variables: `${PAYERID}`, `${TIMESTAMP}`, `${SEQUENCE}`

#### Custom Edits and Enrichments

- **`edits`** (array): Custom claim edits
  - Max items: 8
  - Each edit has:
    - `editId` (string): Unique identifier
    - `description` (string): Edit rule description
    - `severity` (enum): `"error"`, `"warning"`, `"info"` (default: `"error"`)

- **`enrichments`** (array): Custom data enrichments
  - Max items: 5
  - Each enrichment has:
    - `enrichmentId` (string): Unique identifier
    - `description` (string): Enrichment description
    - `dataSource` (string): Source of enrichment data

#### Response Reports

- **`responseReports`** (object): Response report configuration
  - `functional999Supported` (boolean): Support 999 acknowledgments (default: false)
  - `claimAck277CASupported` (boolean): Support 277CA claim acknowledgments (default: false)
  - `responseTimeframe` (string): Expected response timeframe
    - Examples: `"24-48 hours"`, `"2-5 business days"`

---

### Eligibility 270/271 Module

**Configuration Key**: `modules.eligibility270271`

#### Core Settings

- **`enabled`** (boolean, required): Enable Eligibility 270/271 module
- **`transactionModes`**: All modes supported

#### Search Options

- **`searchOptions`** (array, required): Supported search patterns
  - Valid values:
    - `"member_id_dob"` - Member ID + Date of Birth
    - `"member_id_name"` - Member ID + Name
    - `"ssn_dob"` - SSN + Date of Birth
    - `"ssn_name"` - SSN + Name
    - `"name_dob_gender"` - Name + Date of Birth + Gender
    - `"subscriber_id"` - Subscriber ID
  - Constraints: 1-6 unique items
  - **Business Rule**: At least one search option must be specified

#### Service Codes

- **`serviceCodes`** (string or array): Supported service type codes
  - Options:
    - String: `"all"` (all service codes supported)
    - Array: List of 2-digit service type codes (e.g., `["30", "35"]`)
  - Common codes:
    - `"30"` - Health Benefit Plan Coverage
    - `"35"` - Dental Care
    - `"47"` - Hospital - Inpatient
    - `"50"` - Hospital - Outpatient

#### Patient ID Format

- **`patientIdFormat`** (object): Patient ID validation
  - `pattern` (string): Regex pattern for validation
  - `minLength` (integer, 1-50): Minimum length
  - `maxLength` (integer, 1-50): Maximum length

#### Additional Settings

- **`requiresGender`** (boolean): Gender required for eligibility requests
  - Default: `false`

- **`xmlWrapper`** (boolean): Use XML wrapper for requests
  - Default: `false`

---

### Claim Status 276/277 Module

**Configuration Key**: `modules.claimStatus276277`

#### Core Settings

- **`enabled`** (boolean, required): Enable Claim Status 276/277 module
- **`transactionModes`**: All modes supported

#### Configuration

- **`atypicalProvidersAllowed`** (boolean): Allow atypical providers
  - Default: `false`

- **`patientAccountNumberDefault`** (string): Default patient account number if not provided

- **`serviceDateRange`** (object): Service date range constraints
  - `earliest` (string): Earliest service date
    - Examples: `"-365 days"` (relative), `"2020-01-01"` (absolute)
  - `latest` (string): Latest service date
    - Examples: `"today"` (relative), `"+30 days"` (future date)

---

### Attachments 275 Module

**Configuration Key**: `modules.attachments275`

#### Core Settings

- **`enabled`** (boolean, required): Enable Attachments 275 module
- **`transactionModes`**: All modes supported

#### Required Fields

- **`requiredFields`** (array): Required fields for attachment submissions
  - Valid values:
    - `"claimNumber"` - Claim number
    - `"memberName"` - Member name
    - `"memberId"` - Member ID
    - `"dateOfBirth"` - Date of birth
    - `"providerName"` - Provider name
    - `"providerNPI"` - Provider NPI
    - `"serviceDate"` - Service date
    - `"attachmentType"` - Attachment type

#### File Constraints

- **`allowedFileTypes`** (array): Allowed file extensions
  - Valid values: `".pdf"`, `".tif"`, `".tiff"`, `".jpg"`, `".jpeg"`, `".png"`, `".gif"`
  - Default: `[".pdf", ".tif", ".tiff"]`

- **`maxFileSize`** (integer): Maximum file size in bytes
  - Range: 1MB (1048576) - 100MB (104857600)
  - Default: 10MB (10485760)

- **`maxFilesPerTransaction`** (integer): Maximum number of files per transaction
  - Range: 1-50
  - Default: 5

- **`passwordProtectedAllowed`** (boolean): Allow password-protected files
  - Default: `false`

---

### ECS (Enhanced Claim Status) Module

**Configuration Key**: `modules.ecs`

#### Core Settings

- **`enabled`** (boolean, required): Enable ECS module
- **`transactionModes`**: Real-time web and B2B only (no batch mode)

#### Query Methods

- **`queryMethods`** (array, required): Supported query methods
  - Valid values:
    - `"ServiceDate"` - Query by service date range
    - `"Member"` - Query by member information
    - `"CheckNumber"` - Query by check/EFT number
    - `"ClaimHistory"` - Query by claim control number
  - Constraints: 1+ unique items
  - **Business Rule**: At least one query method must be specified

#### Additional Properties

- **`includeProperties`** (array): Additional ECS properties to include in responses
  - Valid values:
    - `"claimStatus"` - Claim status information
    - `"paymentAmount"` - Payment amount
    - `"paymentDate"` - Payment date
    - `"checkNumber"` - Check/EFT number
    - `"remittanceAdvice"` - Remittance advice details
    - `"denialReason"` - Denial reason codes
    - `"adjustmentReason"` - Adjustment reason codes
    - `"paidToProvider"` - Amount paid to provider
    - `"patientResponsibility"` - Patient responsibility amount

#### Caching

- **`cacheEnabled`** (boolean): Enable caching for ECS responses
  - Default: `false`

- **`cacheTTL`** (integer): Cache time-to-live in seconds
  - Range: 60-86400 (1 minute to 24 hours)
  - Default: 3600 (1 hour)
  - **Business Rule**: Should be set when `cacheEnabled` is `true`

#### ValueAdds277 Enhancement

The **ValueAdds277** configuration enables enhanced claim status responses with 60+ additional fields beyond standard X12 277 Healthcare Claim Status Response. This feature transforms basic claim status lookups into comprehensive claim intelligence with seamless cross-module integration.

**Configuration Key**: `modules.ecs.valueAdds277`

##### ValueAdds277 Core Settings

- **`enabled`** (boolean): Enable ValueAdds277 enhanced response fields
  - Default: `true`
  - **Business Value**: Adds 60+ fields including financial breakdown, clinical context, demographics, remittance details, and integration flags

##### Claim-Level Field Groups

Configuration Key: `modules.ecs.valueAdds277.claimFields`

- **`financial`** (boolean): Include financial fields (8 fields)
  - Fields: `BILLED`, `ALLOWED`, `INSURANCE_TOTAL_PAID`, `PATIENT_RESPONSIBILITY`, `COPAY`, `COINSURANCE`, `DEDUCTIBLE`, `DISCOUNT`
  - Default: `true`
  - **Use Case**: Complete financial breakdown for provider accounting

- **`remittance`** (boolean): Include remittance fields (4 fields)
  - Fields: `payeeName`, `checkNumber`, `checkCashedDate`, `checkAmount`
  - Default: `true`
  - **Use Case**: Payment tracking and reconciliation

- **`clinical`** (boolean): Include clinical fields (4 fields)
  - Fields: `drgCode`, `facilityTypeCodeDescription`, `diagnosisCodes`, `code` (reason/remark codes)
  - Default: `true`
  - **Use Case**: Clinical context for claim adjudication decisions

- **`demographics`** (boolean): Include demographic objects (4 objects, 20+ subfields)
  - Objects: `patient`, `subscriber`, `billingProvider`, `renderingProvider`
  - Default: `true`
  - **Use Case**: Complete member and provider information for verification

- **`statusDetails`** (boolean): Include enhanced status fields (5 fields)
  - Fields: `statusCode`, `statusCodeDescription`, `effectiveDate`, `exchangeDate`, `comment`
  - Default: `true`
  - **Use Case**: Detailed status tracking with payer commentary

##### Service Line Field Configuration

Configuration Key: `modules.ecs.valueAdds277.serviceLineFields`

- **`enabled`** (boolean): Include service line details
  - Default: `true`
  - **Impact**: 10+ fields per service line

- **`includeFinancials`** (boolean): Include line-level financial fields
  - Fields: `DEDUCTIBLE`, `DISCOUNT`, `COPAY`, `COINSURANCE`, `PATIENT_RESPONSIBILITY`
  - Default: `true`
  - **Use Case**: Line-by-line financial breakdown

- **`includeModifiers`** (boolean): Include procedure modifiers array
  - Default: `true`
  - **Use Case**: Understanding procedure coding nuances

##### Integration Eligibility Flags

Configuration Key: `modules.ecs.valueAdds277.integrationFlags`

These flags enable seamless one-click workflows between ECS and other modules:

- **`attachments`** (boolean): Enable `eligibleForAttachment` flag
  - Default: `true`
  - **Logic**: `true` if claim status is Pending, In Process, or Suspended
  - **Integration**: Enables "Send Attachments" button → HIPAA 275 Attachments workflow
  - **Use Case**: Provider can send additional documentation for pending claims

- **`corrections`** (boolean): Enable `eligibleForCorrection` flag
  - Default: `true`
  - **Logic**: `true` if claim status is Denied or Rejected
  - **Integration**: Enables "Correct and Resubmit" button → Corrections workflow
  - **Use Case**: Provider can correct errors and resubmit denied claims

- **`appeals`** (boolean): Enable `eligibleForAppeal` flag with appeal metadata
  - Default: `true`
  - **Logic**: `true` if claim status is Denied or Partially Paid
  - **Additional Fields**: `appealMessage`, `appealType`, `appealTimelyFilingDate`
  - **Integration**: Enables "Dispute Claim" button → Appeals module (PR #49)
  - **Use Case**: Provider can initiate appeal with pre-populated metadata
  - **Example**: `appealMessage: "This claim may be eligible for appeal. Timely filing deadline: 2024-07-15"`

- **`messaging`** (boolean): Enable `eligibleForMessaging` flag
  - Default: `true`
  - **Logic**: Always `true` (configurable per payer)
  - **Integration**: Enables "Message Payer" button → Secure messaging
  - **Use Case**: Provider can ask questions or request clarification

- **`chat`** (boolean): Enable `eligibleForChat` flag
  - Default: `false`
  - **Logic**: Based on payer's live chat availability
  - **Integration**: Enables "Live Chat" button → Real-time chat with payer
  - **Use Case**: Immediate support for urgent claim questions

- **`remittanceViewer`** (boolean): Enable `eligibleForRemittanceViewer` flag
  - Default: `true`
  - **Logic**: `true` if claim status is Paid or Partially Paid
  - **Integration**: Enables "View Remittance" button → Remittance viewer
  - **Use Case**: Provider can view detailed remittance advice (835)

##### ValueAdds277 Configuration Example

```json
{
  "modules": {
    "ecs": {
      "enabled": true,
      "queryMethods": ["ServiceDate", "Member", "CheckNumber", "ClaimHistory"],
      "valueAdds277": {
        "enabled": true,
        "claimFields": {
          "financial": true,
          "remittance": true,
          "clinical": true,
          "demographics": true,
          "statusDetails": true
        },
        "serviceLineFields": {
          "enabled": true,
          "includeFinancials": true,
          "includeModifiers": true
        },
        "integrationFlags": {
          "attachments": true,
          "corrections": true,
          "appeals": true,
          "messaging": true,
          "chat": false,
          "remittanceViewer": true
        }
      }
    }
  }
}
```

##### Commercialization Notes

**Product Name**: Enhanced Claim Status Plus  
**Pricing**: +$10,000/year per payer (recommended add-on)  
**Value Proposition**:
- 60+ additional fields vs. basic 277 response
- Seamless integration with Appeals, Attachments, Corrections modules
- Reduced provider phone calls (21 minutes saved per claim lookup)
- One-click workflows ("Dispute Claim", "Send Attachments", "Message Payer")
- ROI: $69,600/year for provider making 1,000 claim inquiries/month

**Key Differentiators**:
- Only platform with complete X12 277 ValueAdds277 implementation
- Pre-populated appeal workflows save 81% of provider time
- Real-time integration flags eliminate manual workflow routing

For complete ValueAdds277 documentation, see:
- [ECS-INTEGRATION.md](./ECS-INTEGRATION.md) - Complete field reference and mapping
- [VALUEADDS277-README.md](./VALUEADDS277-README.md) - Quick start guide
- [VALUEADDS277-SUMMARY.md](./VALUEADDS277-SUMMARY.md) - Implementation summary
- [APPEALS-INTEGRATION.md](./APPEALS-INTEGRATION.md) - Appeals module integration
- [COMMERCIALIZATION.md](./COMMERCIALIZATION.md) - Product positioning

---

## Validation Rules and Constraints

### JSON Schema Validation

The schema uses JSON Schema Draft-07 with the following validation features:

1. **Type Checking**: All fields have strict type requirements
2. **Required Fields**: Core fields marked as required must be present
3. **Pattern Matching**: String fields with specific formats use regex patterns
4. **Range Constraints**: Numeric fields have min/max values
5. **Enum Values**: Limited-choice fields use enumerations
6. **Conditional Requirements**: Some fields are required based on other field values

### Business Rule Validation

Additional validation beyond schema:

#### State Code Validation
- All state codes must be valid 2-letter US state/territory codes
- No duplicate state codes allowed

#### Appeals Module Rules
- If `additionalClaimsSupported` is `true`, `maxAdditionalClaims` must be > 0
- If `additionalClaimsSupported` is `false`, `maxAdditionalClaims` should be 0
- `requestReasons` array must have at least 1 item
- At least one transaction mode must be enabled

#### Claims 837 Module Rules
- At least one claim type must be enabled
- Maximum 8 custom edits
- Maximum 5 custom enrichments
- At least one transaction mode must be enabled

#### Eligibility Module Rules
- `searchOptions` array must have at least 1 item
- At least one transaction mode must be enabled

#### ECS Module Rules
- `queryMethods` array must have at least 1 item
- If `cacheEnabled` is `true`, `cacheTTL` should be specified
- At least one transaction mode must be enabled

#### ValueAdds277 Module Rules
- If `valueAdds277.enabled` is `true`, at least one field group should be enabled
- If `valueAdds277.integrationFlags.appeals` is `true`, Appeals module should be enabled
- If `valueAdds277.integrationFlags.attachments` is `true`, Attachments 275 module should be enabled
- Service line fields (`serviceLineFields.enabled`) should be `true` to maximize provider value
- Integration flags provide maximum ROI when multiple modules are enabled (Appeals + Attachments + Corrections)

#### General Module Rules
- At least one module should be enabled (warning)
- Each enabled module must have at least one enabled transaction mode

---

## Example Configurations

### Example 1: Medicaid MCO Configuration

**File**: `core/examples/medicaid-mco-config.json`

This example demonstrates a nationwide Medicaid Managed Care Organization with:
- Appeals module with pre-appeal attachment pattern
- Attachments 275 module with batch processing
- ECS module with all query methods
- Contact information required for appeals (phone + email)
- Support for additional claims (max 10)

**Key Features**:
- Nationwide deployment
- 8 appeal request reasons
- All 8 sub-statuses
- 64MB max file size
- ECS integration enabled for appeals

### Example 2: Regional Blue Cross Blue Shield Configuration

**File**: `core/examples/regional-blues-config.json`

This example demonstrates a regional BCBS plan with:
- All modules enabled (comprehensive configuration)
- Real-time web + EDI batch modes
- Post-appeal attachment pattern
- State-specific deployment (5 states: TX, OK, LA, AR, NM)
- Multi-claim verification enabled
- Strict character set requirements (uppercase only)

**Key Features**:
- Full 837 claims support (Professional, Institutional, Dental)
- Custom claim edits and enrichments
- Uppercase-only character set
- TA1, 997, and 999 acknowledgments required
- Controlled deployment

### Loading a Configuration

```typescript
import * as fs from 'fs';
import { AvailityIntegrationConfig } from './core/interfaces/availity-integration-config.interface';
import { validateConfig } from './core/validation/config-validator';
import schema from './core/schemas/availity-integration-config.schema.json';

// Load configuration
const configJson = fs.readFileSync('path/to/payer-config.json', 'utf-8');
const config: AvailityIntegrationConfig = JSON.parse(configJson);

// Validate configuration
const result = validateConfig(config, schema);

if (result.valid) {
  console.log('✓ Configuration is valid');
  // Use configuration
} else {
  console.error('Configuration validation failed:');
  result.errors.forEach(error => {
    console.error(`  [${error.field}] ${error.message}`);
  });
}
```

---

## How to Extend the Schema

### Adding a New Module

1. **Define the Module Interface** in `core/interfaces/availity-integration-config.interface.ts`:

```typescript
export interface NewModule {
  enabled?: boolean;
  transactionModes?: TransactionModes;
  // Add module-specific properties
  customProperty?: string;
}
```

2. **Add to Modules Interface**:

```typescript
export interface Modules {
  // ... existing modules
  newModule?: NewModule;
}
```

3. **Define Schema in JSON Schema** in `core/schemas/availity-integration-config.schema.json`:

```json
{
  "definitions": {
    "NewModule": {
      "type": "object",
      "additionalProperties": false,
      "properties": {
        "enabled": {
          "type": "boolean",
          "default": false
        },
        "transactionModes": {
          "type": "object",
          "properties": {
            "realtime_web": { "$ref": "#/definitions/TransactionMode" },
            "realtime_b2b": { "$ref": "#/definitions/TransactionMode" },
            "edi_batch": { "$ref": "#/definitions/TransactionMode" }
          }
        },
        "customProperty": {
          "type": "string",
          "description": "Custom property description"
        }
      }
    }
  }
}
```

4. **Add to modules property**:

```json
{
  "properties": {
    "modules": {
      "type": "object",
      "properties": {
        "newModule": { "$ref": "#/definitions/NewModule" }
      }
    }
  }
}
```

5. **Add Validation Rules** in `core/validation/config-validator.ts`:

```typescript
if (config.modules?.newModule?.enabled) {
  this.validateNewModule(config.modules.newModule, errors, warnings);
}

private validateNewModule(
  newModule: any,
  errors: ValidationError[],
  warnings: ValidationError[]
): void {
  // Add custom validation logic
}
```

### Adding a New Field to Existing Module

1. **Update TypeScript Interface**
2. **Update JSON Schema** with property definition
3. **Add Validation Rules** (if needed)
4. **Update Documentation** (this file)
5. **Update Example Configurations** to demonstrate the new field

### Versioning the Schema

Use the `$id` field in the JSON Schema to version:

```json
{
  "$id": "https://aurelianware.com/schemas/availity-integration-config/v2.0.json"
}
```

Maintain backward compatibility by:
- Making new fields optional
- Providing default values
- Supporting legacy field names with deprecation warnings

---

## Migration Guide

### From Hardcoded Payer Configurations

#### Step 1: Identify Current Payer Configuration

Review existing code for hardcoded values:
- Payer IDs and names
- Connection URLs and endpoints
- ISA/GS segment values
- Module-specific settings
- Contact information

#### Step 2: Create Configuration File

Create a new JSON configuration file using the schema:

```json
{
  "$schema": "core/schemas/availity-integration-config.schema.json",
  "organizationName": "Your Health Plan",
  "payerId": "YOUR_PAYER_ID",
  "payerName": "Display Name",
  "contacts": {
    "technical": { ... },
    "accountManager": { ... },
    "escalation": { ... }
  },
  "modules": { ... }
}
```

#### Step 3: Populate Module Configurations

For each enabled module, configure:
- Transaction modes (URLs, FTP settings)
- Module-specific properties
- File constraints
- Business rules

#### Step 4: Validate Configuration

```bash
# Use validator to check configuration
node validate-config.js path/to/payer-config.json
```

#### Step 5: Update Application Code

Replace hardcoded values with configuration loading:

**Before**:
```typescript
const payerId = "12345";
const apiUrl = "https://api.payer.com/claims";
```

**After**:
```typescript
const config = loadConfig('payer-config.json');
const payerId = config.payerId;
const apiUrl = config.modules.claims837.transactionModes.realtime_web.prodUrl;
```

#### Step 6: Test in Non-Production

1. Deploy to test environment
2. Validate all transaction types work correctly
3. Verify error handling
4. Test failover scenarios

#### Step 7: Deploy to Production

1. Backup current configuration
2. Deploy new configuration-driven code
3. Monitor for issues
4. Rollback plan ready

### From Legacy Configuration Format

If you have an existing configuration format:

1. **Create Mapping Document**: Map old fields to new schema fields
2. **Build Migration Script**: Automate conversion from old to new format
3. **Validate Migrated Configurations**: Ensure all data is preserved
4. **Run Side-by-Side**: Test both formats in parallel
5. **Cut Over**: Switch to new format after validation

---

## Best Practices

### Configuration Management

1. **Version Control**: Store all configurations in Git
2. **Environment Separation**: Separate files for test/prod
3. **Validation in CI/CD**: Validate on every commit
4. **Change Reviews**: Require review for config changes
5. **Documentation**: Document payer-specific customizations

### Security

1. **No Secrets in Config**: Store credentials separately (Key Vault, environment variables)
2. **Access Control**: Limit who can modify configurations
3. **Audit Trail**: Track all configuration changes
4. **Encryption**: Encrypt sensitive configuration data at rest

### Maintenance

1. **Regular Reviews**: Review configurations quarterly
2. **Update Schema**: Add new features to schema as needed
3. **Deprecation**: Mark deprecated fields with warnings
4. **Migration Support**: Provide tools for schema upgrades

---

## Developer Extension Guide

### Adding a New Module to the Schema

The configuration schema is designed to be extensible. Follow these steps to add a new module:

#### Step 1: Define Module Schema

Create a new JSON schema file for your module in `core/schemas/modules/`:

```json
{
  "$schema": "http://json-schema.org/draft-07/schema#",
  "title": "CustomModule Configuration",
  "type": "object",
  "properties": {
    "enabled": {
      "type": "boolean",
      "description": "Enable/disable the custom module"
    },
    "apiEndpoint": {
      "type": "string",
      "format": "uri",
      "description": "API endpoint for custom module"
    },
    "backend": {
      "type": "object",
      "properties": {
        "apiBaseUrl": { "type": "string", "format": "uri" },
        "authType": { "type": "string", "enum": ["OAuth2", "ApiKey", "ManagedIdentity"] },
        "timeout": { "type": "string", "pattern": "^[0-9]+s$" }
      }
    },
    "fieldMappings": {
      "type": "object",
      "additionalProperties": { "type": "string" }
    }
  },
  "required": ["enabled"]
}
```

#### Step 2: Add Module to Root Schema

Update `availity-integration-config.schema.json`:

```json
{
  "type": "object",
  "properties": {
    "organizationName": { "type": "string" },
    "payerId": { "type": "string" },
    "customModule": {
      "$ref": "./modules/customModule.schema.json"
    }
  }
}
```

#### Step 3: Create Workflow Generator

Implement generator in `generators/customModuleGenerator.ts`:

```typescript
import { PayerConfig, Workflow } from '../types';

export function generateCustomModuleWorkflow(config: PayerConfig): Workflow {
  const moduleConfig = config.customModule;
  
  return {
    definition: {
      $schema: "https://schema.management.azure.com/providers/Microsoft.Logic/schemas/2016-06-01/workflowdefinition.json#",
      triggers: {
        httpTrigger: {
          type: "Request",
          kind: "Http",
          inputs: {
            schema: getModuleRequestSchema(moduleConfig)
          }
        }
      },
      actions: {
        callBackendApi: {
          type: "Http",
          inputs: {
            method: "POST",
            uri: moduleConfig.backend.apiBaseUrl,
            authentication: getAuthConfig(moduleConfig),
            body: transformRequest(moduleConfig.fieldMappings)
          }
        },
        transformResponse: {
          type: "Compose",
          inputs: transformResponse(moduleConfig.fieldMappings),
          runAfter: { callBackendApi: ["Succeeded"] }
        }
      }
    },
    kind: "Stateful",
    parameters: buildParameters(moduleConfig)
  };
}
```

#### Step 4: Add Validation

Create validator in `validators/customModuleValidator.ts`:

```typescript
export function validateCustomModule(config: any): ValidationResult {
  const errors: ValidationError[] = [];
  
  if (config.customModule?.enabled) {
    if (!config.customModule.backend?.apiBaseUrl) {
      errors.push({
        path: "customModule.backend.apiBaseUrl",
        message: "API base URL required when module is enabled"
      });
    }
    
    if (config.customModule.backend?.authType === "OAuth2") {
      if (!config.customModule.backend.clientIdSecretName) {
        errors.push({
          path: "customModule.backend.clientIdSecretName",
          message: "Client ID secret name required for OAuth2 authentication"
        });
      }
    }
  }
  
  return { valid: errors.length === 0, errors };
}
```

#### Step 5: Update CLI and Documentation

Add module support to CLI:
```typescript
// cli/payer-generator-cli.ts
import { generateCustomModuleWorkflow } from '../generators/customModuleGenerator';

// Add to workflow generation
if (config.customModule?.enabled) {
  workflows.push(generateCustomModuleWorkflow(config));
}
```

Create module documentation:
- `docs/CUSTOM-MODULE-INTEGRATION.md` - Integration guide
- `docs/examples/custom-module-config.json` - Example configuration
- Add to `docs/ARCHITECTURE.md` - Architecture diagram

### Best Practices for Extensions

1. **Backward Compatibility**: Always maintain compatibility with previous schema versions
2. **Validation**: Add comprehensive validation rules and error messages
3. **Documentation**: Document all fields with descriptions and examples
4. **Testing**: Write unit and integration tests for generator and validator
5. **Defaults**: Provide sensible defaults where appropriate
6. **Field Mappings**: Support flexible field mappings for backend integration
7. **Error Handling**: Implement proper error handling in generated workflows

### Testing Extensions

Test your extensions thoroughly:

```typescript
// tests/generators/customModuleGenerator.test.ts
describe("CustomModule Generator", () => {
  it("should generate valid workflow for enabled module", () => {
    const config = {
      customModule: {
        enabled: true,
        backend: {
          apiBaseUrl: "https://api.example.com",
          authType: "OAuth2"
        }
      }
    };
    
    const workflow = generateCustomModuleWorkflow(config);
    
    expect(workflow.definition.triggers).toBeDefined();
    expect(workflow.definition.actions.callBackendApi).toBeDefined();
    expect(workflow.kind).toBe("Stateful");
  });
});
```

---

## Support and Resources

- **Schema File**: `core/schemas/availity-integration-config.schema.json`
- **TypeScript Interfaces**: `core/interfaces/availity-integration-config.interface.ts`
- **Validator**: `core/validation/config-validator.ts`
- **Examples**: `core/examples/`
- **Generator**: `scripts/cli/payer-generator-cli.js`
- **Documentation**: See [CONFIG-TO-WORKFLOW-GENERATOR.md](./CONFIG-TO-WORKFLOW-GENERATOR.md)

For questions or issues:
1. Review this documentation
2. Check example configurations
3. Validate your configuration file
4. Run generator with `--dry-run` flag
5. Contact the integration team

---

**Last Updated**: 2025-11-20  
**Schema Version**: 2.0  
**Document Version**: 2.0
