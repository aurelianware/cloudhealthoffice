> **Note:** This document references Azure Logic Apps, which were the original orchestration runtime. CHO has since migrated to Argo Workflows on AKS — see [ADR-004](../adr/004-remove-logic-apps.md) for details.

# HIPAA X12 275/277/278 Agreements Configuration Guide
# Clearinghouse ↔ Health Plan (Health Plan) / claims backend System

## 🏥 Healthcare EDI Workflow Overview

**Business Process**: HIPAA Attachment Processing
- **275 Message**: Attachment Request (Clearinghouse → Health Plan)
- **277 Message**: Attachment Response (Health Plan → Clearinghouse)  
- **278 Message**: Health Care Services Review Information (Processing & Replay)
- **Backend System**: claims backend (Health Plan's claims processing system)

## 🤝 Trading Partners Configuration ✅ COMPLETED

| Partner | Name | ID | Qualifier | Role |
|---------|------|----|-----------|----- |
| **Clearinghouse** | Clearinghouse | 030240928 | ZZ | Sender (275) / Receiver (277) |
| **Health Plan** | Health Plan Backend | {config.payerId} | ZZ | Receiver (275) / Sender (277) |

## 📋 Required X12 Agreements

### 1️⃣ X12 275 RECEIVE Agreement
**Purpose**: Process incoming attachment requests from the clearinghouse

**Configuration**:
- **Agreement Name**: `Clearinghouse-to-Health Plan-275-Receive`
- **Host Partner**: Health Plan Backend (you/receiver)
- **Guest Partner**: Clearinghouse (sender)
- **Protocol**: X12
- **Direction**: Receive (Inbound)

**Key Settings**:
- **ISA Sender ID**: {config.clearinghouseId} (Clearinghouse)
- **ISA Receiver ID**: {config.payerId} (Health Plan)
- **GS Sender ID**: CLEARINGHOUSE
- **GS Receiver ID**: Health Plan or claims backend
- **Transaction Type**: 275 (Additional Information to Support a Healthcare Claim)
- **Version**: 005010X215 (HIPAA version)

**Message Flow**: Clearinghouse SFTP → Logic App → Decode X12 275 → Process Attachments

### 2️⃣ X12 277 SEND Agreement
**Purpose**: Send attachment responses back to the clearinghouse

**Configuration**:
- **Agreement Name**: `Health Plan-to-Clearinghouse-277-Send`
- **Host Partner**: Health Plan Backend (you/sender)
- **Guest Partner**: Clearinghouse (receiver)
- **Protocol**: X12
- **Direction**: Send (Outbound)

**Key Settings**:
- **ISA Sender ID**: {config.payerId} (Health Plan)
- **ISA Receiver ID**: {config.clearinghouseId} (Clearinghouse)
- **GS Sender ID**: Health Plan or claims backend
- **GS Receiver ID**: CLEARINGHOUSE
- **Transaction Type**: 277 (Healthcare Information Status Notification)
- **Version**: 005010X212 (HIPAA version)

**Message Flow**: claims backend response → Logic App → Encode X12 277 → Send to the clearinghouse

### 3️⃣ X12 278 RECEIVE Agreement
**Purpose**: Process health care services review information and support replay functionality

**Configuration**:
- **Agreement Name**: `Health Plan-278-Processing`
- **Host Partner**: Health Plan Backend (you/receiver)
- **Guest Partner**: Health Plan Backend (internal processing)
- **Protocol**: X12
- **Direction**: Receive (Internal Processing)

**Key Settings**:
- **ISA Sender ID**: {config.payerId} (Health Plan)
- **ISA Receiver ID**: {config.payerId} (Health Plan - internal)
- **GS Sender ID**: Health Plan or claims backend
- **GS Receiver ID**: Health Plan or claims backend
- **Transaction Type**: 278 (Health Care Services Review Information)
- **Version**: 005010X217 (HIPAA version)

**Message Flow**: Service Bus edi-278 Topic → Logic App → Decode X12 278 → Process Review → claims backend API

**📋 Integration Account Schema Requirements**:
- Upload X12 278 schema (005010X217_278.xsd)
- Configure agreement for internal processing and replay scenarios

## 🔧 Azure Portal Configuration Steps

### Step 1: Access Integration Account
1. Go to: https://portal.azure.com
2. Navigate to: Resource Groups → `rg-hipaa-logic-apps` → `hipaa-attachments-ia`
3. Click: **Agreements** (in the left menu)

### Step 2: Create 275 Receive Agreement
1. Click **+ Add**
2. **Agreement Name**: `Clearinghouse-to-Health Plan-275-Receive`
3. **Agreement Type**: X12
4. **Host Partner**: Select `Health Plan Backend`
5. **Guest Partner**: Select the clearinghouse
6. **Host Identity**: 
   - Qualifier: ZZ
   - Value: {config.payerId}
7. **Guest Identity**:
   - Qualifier: ZZ  
   - Value: 030240928

**Receive Settings**:
- **Identifiers**: 
  - ISA1: 00 (No Authorization)
  - ISA3: 00 (No Security)
- **Acknowledgments**:
  - ☑️ TA1 Expected (Technical Ack)
  - ☑️ FA Expected (Functional Ack - 997)
- **Schemas**: Upload or select X12 275 schema
- **Envelopes**:
  - ISA11: U (Production) or T (Test)
  - GS08: 005010X215

### Step 3: Create 277 Send Agreement
1. Click **+ Add**
2. **Agreement Name**: `Health Plan-to-Clearinghouse-277-Send`
3. **Agreement Type**: X12
4. **Host Partner**: Select `Health Plan Backend`
5. **Guest Partner**: Select the clearinghouse
6. **Host Identity**: 
   - Qualifier: ZZ
   - Value: {config.payerId}
7. **Guest Identity**:
   - Qualifier: ZZ
   - Value: 030240928

**Send Settings**:
- **Identifiers**:
  - ISA1: 00 (No Authorization)
  - ISA3: 00 (No Security)
- **Acknowledgments**:
  - ☑️ Request TA1 (Technical Ack)
  - ☑️ Request FA (Functional Ack - 997)
- **Schemas**: Upload or select X12 277 schema
- **Envelopes**:
  - ISA11: U (Production) or T (Test)
  - GS08: 005010X212

## 📁 Required HIPAA X12 Schemas

You'll need to upload these standard HIPAA schemas:

### For 275 Processing (Inbound):
- **275.xsd**: Additional Information to Support a Healthcare Claim
- **Common schemas**: HIPAA-Common, X12-Common

### For 277 Processing (Outbound):
- **277.xsd**: Healthcare Information Status Notification  
- **Common schemas**: HIPAA-Common, X12-Common

### For 278 Processing (Internal):
- **278.xsd**: Health Care Services Review Information (005010X217)
- **Common schemas**: HIPAA-Common, X12-Common

**Schema Sources**:
- Microsoft HIPAA Accelerator
- Washington Publishing Company (WPC)
- X12.org official schemas

## 🔄 Message Flow Summary

```
1. 275 Inbound (Attachment Request):
   Clearinghouse SFTP → Logic App Trigger → X12 Decode (275) → Extract Attachments → Store in Data Lake

2. 277 Outbound (Status Response):  
   claims backend Processing → Logic App → X12 Encode (277) → Send to the clearinghouse

3. 278 Processing (Review Information):
   Service Bus edi-278 Topic → Logic App Trigger → X12 Decode (278) → Extract Review Data → claims backend API

4. 278 Replay (Deterministic):
   HTTP Request → Validate Blob URL → Queue to edi-278 Topic → Process via ingest278 workflow
```

## ⚠️ Important HIPAA Compliance Notes

- **Encryption**: All messages must be encrypted in transit and at rest
- **Audit Logging**: Enable detailed tracking for all EDI transactions
- **Access Control**: Restrict access to authorized personnel only
- **Data Retention**: Follow HIPAA requirements for medical record retention
- **BAA Required**: Ensure Business Associate Agreement with the clearinghouse

## 🧪 Testing Recommendations

1. **Start with Test Environment**: Use ISA11 = 'T' for testing
2. **Sample 275 Messages**: Obtain test files from the clearinghouse
3. **Validation**: Verify schema compliance before production
4. **End-to-End Testing**: Test complete workflow with claims backend integration

## 📞 Support Contacts

- **Clearinghouse Support**: For 275 message formats and connectivity
- **claims backend Support**: For backend integration and 277 responses  
- **Azure Support**: For Integration Account and Logic Apps issues

---
*Generated: September 24, 2025*
*Integration Account: hipaa-attachments-ia*
*Environment: Azure West US*