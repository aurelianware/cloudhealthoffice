> **Note:** This document references Azure Logic Apps, which were the original orchestration runtime. CHO has since migrated to Argo Workflows on AKS — see [ADR-004](../adr/004-remove-logic-apps.md) for details.

# HIPAA X12 275/277 Trading Partners Testing Plan
# Clearinghouse (030240928) ↔ Health Plan Backend ({config.payerId})

## 🎯 Testing Overview

This comprehensive test plan will verify the end-to-end HIPAA attachment processing workflow using the configured trading partners.

## 📋 Test Environment Setup

### Prerequisites Checklist:
- ✅ Integration Account: `hipaa-attachments-ia`
- ✅ Trading Partners: Clearinghouse (030240928) & Health Plan Backend ({config.payerId})  
- ⚠️ X12 Agreements: Need to be created in Azure Portal
- ✅ Logic Apps: ingest275 & rfai277 workflows
- ✅ Service Bus: hipaa-attachments-svc with topics
- ✅ Data Lake Storage: hipaa7v2rrsoo6tac2

## 🧪 Test Cases

### Test Case 1: X12 275 Inbound Processing
**Objective**: Verify Clearinghouse → Health Plan attachment request processing

**Test Data**: Sample X12 275 EDI Message
```edi
ISA*00*          *00*          *ZZ*030240928      *ZZ*{config.payerId}          *250924*1425*^*00501*000000001*0*T*:~
GS*HI*030240928*{config.payerId}*20250924*1425*1*X*005010X215~
ST*275*0001*005010X215~
BHT*0085*08*RFAI20250924001*20250924*1425~
HL*1**20*1~
NM1*PR*2*SAMPLE HEALTH PLAN*****PI*{config.payerId}~
HL*2*1*22*1~
NM1*IL*1*SMITH*JOHN*A***MI*123456789~
HL*3*2*23*0~
CLM*CLM20250924001*100.00***11:B:1*Y*A*Y*I~
DTP*472*D8*20250901~
PWK*OZ*EL***AC*ATTACHMENT001~
REF*D9*RFAI20250924001~
TRN*1*TRACE20250924001*9030240928~
SE*12*0001~
GE*1*1~
IEA*1*000000001~
```

**Expected Results**:
1. ✅ File stored in Data Lake: `/raw/275/2025/09/24/`
2. ✅ X12 decoded successfully using Clearinghouse-Health Plan agreement
3. ✅ Metadata extracted: Claim CLM20250924001, Member 123456789
4. ✅ Message sent to Service Bus topic: attachments-in
5. ✅ claims backend API called with claim linkage data

### Test Case 2: X12 277 Outbound Generation
**Objective**: Verify Health Plan → Clearinghouse response processing

**Test Data**: claims backend response Payload
```json
{
  "claimNumber": "CLM20250924001",
  "memberId": "123456789",
  "providerNpi": "1234567890",
  "serviceFromDate": "20250901",
  "rfaiReasonCode": "A7",
  "rfaiReference": "RFAI20250924001",
  "status": "received",
  "attachmentPath": "/processed/275/2025/09/24/attachment001.pdf"
}
```

**Expected Results**:
1. ✅ Service Bus message received from rfai-requests topic
2. ✅ X12 277 constructed with Health Plan as sender, Clearinghouse as receiver
3. ✅ Message encoded using Health Plan-Clearinghouse agreement
4. ✅ Response transmitted back to the clearinghouse endpoint

## 🚀 Execute Tests

### Phase 1: Deploy and Verify Logic Apps

First, let me check the current Logic Apps deployment and update the trading partner references:

```powershell
# Deploy updated Logic Apps with correct trading partner IDs
az deployment group create --resource-group "rg-hipaa-logic-apps" --template-file "infra/main.bicep"
```

### Phase 2: Create Test Files

Let me create realistic test files for both scenarios.