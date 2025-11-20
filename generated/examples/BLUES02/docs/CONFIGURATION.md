# Configuration Guide - Regional Blue Cross Blue Shield

## Configuration Overview

This document describes the configuration for Regional Blue Cross Blue Shield (BLUES02).

## Contact Information

- **Primary Contact**: Jane Doe
- **Email**: jane.doe@blues02.com
- **Phone**: +1-555-0200
- **Support Email**: edi-support@blues02.com

## Module Configuration

### Enabled Modules

- **appeals**: Enabled
- **ecs**: Enabled
- **attachments**: Enabled
- **authorizations**: Enabled


### Appeals Configuration

- **Test Endpoint**: https://test-gateway.blues02.com/appeals/api/v2
- **Prod Endpoint**: https://gateway.blues02.com/appeals/api/v2
- **Authentication**: apikey
- **Timeout**: 90000ms
- **Retry Count**: 4
- **Retry Interval**: 3000ms

#### Request Reasons

- **MED**: Medical Necessity Review (Attachments: Required)
- **COV**: Coverage Determination (Attachments: Required)
- **TFL**: Timely Filing Limit (Attachments: Optional)
- **COD**: Coding/Billing Error (Attachments: Required)
- **DUP**: Duplicate Payment (Attachments: Optional)

#### Sub-Statuses

- **REGISTERED**: Appeal registered in system 
- **PENDING_DOCS**: Awaiting additional documentation 
- **UNDER_REVIEW**: Clinical review in progress 
- **PEER_REVIEW**: Escalated to peer review 
- **OVERTURN**: Original decision overturned (Final)
- **UPHELD**: Original decision upheld (Final)
- **PARTIAL**: Partial approval (Final)
- **CANCELLED**: Appeal cancelled (Final)

#### Attachment Rules

- **Pattern**: post-appeal
- **Max File Size**: 20.00 MB
- **Allowed Formats**: pdf, jpg, jpeg, png, tif, tiff, doc, docx, xml
- **Max Attachments**: 15



### ECS Configuration

- **Test Endpoint**: https://test-gateway.blues02.com/ecs/api/v1
- **Prod Endpoint**: https://gateway.blues02.com/ecs/api/v1
- **Authentication**: apikey
- **Timeout**: 45000ms
- **Retry Count**: 2

#### Search Methods

- **serviceDate**: Enabled
- **member**: Enabled
- **checkNumber**: Enabled
- **claimHistory**: Disabled



### Attachments Configuration

#### SFTP Configuration

- **Host**: edi-sftp.blues02.com
- **Port**: 22
- **Username**: hipaa_x12_user
- **Inbound Folder**: /x12/inbound
- **Outbound Folder**: /x12/outbound

#### X12 Configuration

- **Sender ID**: BLUES02
- **Receiver ID**: AVAILITY
- **Transaction Sets**: 275, 277, 278


## Infrastructure Configuration

- **Resource Name Prefix**: blues02
- **Location**: centralus
- **Environment**: prod
- **Logic App SKU**: WS1
- **Worker Count**: 3
- **Always On**: true

## Tags

- **Project**: EDI-Integration
- **Payer**: BLUES02
- **Environment**: Production
- **CostCenter**: EDI-Operations
- **Compliance**: HIPAA
