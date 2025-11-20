# Configuration Guide - State Medicaid MCO

## Configuration Overview

This document describes the configuration for State Medicaid MCO (MCO001).

## Contact Information

- **Primary Contact**: John Smith
- **Email**: john.smith@mco001.com
- **Phone**: +1-555-0100
- **Support Email**: support@mco001.com

## Module Configuration

### Enabled Modules

- **appeals**: Enabled
- **ecs**: Enabled
- **attachments**: Enabled
- **authorizations**: Enabled


### Appeals Configuration

- **Test Endpoint**: https://test-api.mco001.com/appeals/v1
- **Prod Endpoint**: https://api.mco001.com/appeals/v1
- **Authentication**: oauth
- **Timeout**: 120000ms
- **Retry Count**: 3
- **Retry Interval**: 5000ms

#### Request Reasons

- **01**: Medical Necessity (Attachments: Required)
- **02**: Timely Filing (Attachments: Optional)
- **03**: Coding Error (Attachments: Required)
- **04**: Duplicate Claim (Attachments: Optional)

#### Sub-Statuses

- **RECEIVED**: Appeal received and registered 
- **IN_REVIEW**: Under medical review 
- **PENDED**: Additional information requested 
- **APPROVED**: Appeal approved (Final)
- **DENIED**: Appeal denied (Final)
- **WITHDRAWN**: Appeal withdrawn by provider (Final)

#### Attachment Rules

- **Pattern**: pre-appeal
- **Max File Size**: 10.00 MB
- **Allowed Formats**: pdf, jpg, png, tiff, doc, docx
- **Max Attachments**: 10



### ECS Configuration

- **Test Endpoint**: https://test-api.mco001.com/ecs/v1
- **Prod Endpoint**: https://api.mco001.com/ecs/v1
- **Authentication**: oauth
- **Timeout**: 60000ms
- **Retry Count**: 3

#### Search Methods

- **serviceDate**: Enabled
- **member**: Enabled
- **checkNumber**: Enabled
- **claimHistory**: Enabled



### Attachments Configuration

#### SFTP Configuration

- **Host**: sftp.mco001.com
- **Port**: 22
- **Username**: hipaa_attachments
- **Inbound Folder**: /inbound/attachments
- **Outbound Folder**: /outbound/responses

#### X12 Configuration

- **Sender ID**: MCO001
- **Receiver ID**: AVAILITY
- **Transaction Sets**: 275, 277, 278


## Infrastructure Configuration

- **Resource Name Prefix**: mco001
- **Location**: eastus
- **Environment**: dev
- **Logic App SKU**: WS1
- **Worker Count**: 1
- **Always On**: true

## Tags

- **Project**: HIPAA-Attachments
- **Payer**: MCO001
- **Environment**: Dev
- **CostCenter**: IT-Integration
