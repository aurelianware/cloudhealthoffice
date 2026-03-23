> **Note:** This document references Azure Logic Apps, which were the original orchestration runtime. CHO has since migrated to Argo Workflows on AKS — see [ADR-004](../adr/004-remove-logic-apps.md) for details.

# 🎯 HIPAA Trading Partners Testing - Status Report

**Generated**: September 24, 2025  
**Testing Environment**: Azure West US  
**Integration Account**: hipaa-attachments-ia

## ✅ Configuration Status

### Trading Partners - CONFIGURED ✅
| Partner | Role | ID | Qualifier | Status |
|---------|------|----|-----------|----- |
| **Clearinghouse** | EDI Clearinghouse | 030240928 | ZZ | ✅ Active |
| **Health Plan Backend** | Health Plan System | {config.payerId} | ZZ | ✅ Active |

### Azure Infrastructure - DEPLOYED ✅
| Resource | Name | Status | Purpose |
|----------|------|--------|---------|
| **Integration Account** | hipaa-attachments-ia | ✅ Active | X12 EDI Processing |
| **Logic Apps** | hipaa-attachments-la | ✅ Deployed | Workflow Orchestration |
| **Service Bus** | hipaa-attachments-svc | ✅ Active | Message Queuing |
| **Storage Account** | hipaa7v2rrsoo6tac2 | ✅ Active | Data Lake Gen2 |
| **Topics** | attachments-in, rfai-requests | ✅ Active | Message Routing |

### Test Data - READY ✅
| File | Purpose | Status |
|------|---------|--------|
| **test-x12-275-clearinghouse-inbound.edi** | Inbound 275 from the clearinghouse | ✅ Created |
| **test-backend-response-payload.json** | claims backend response for 277 | ✅ Created |

## ⚠️ MISSING: X12 Agreements

**CRITICAL**: You need to create X12 agreements in the Azure Portal before testing can proceed.

### Required Agreements:

#### 1️⃣ X12 275 Receive Agreement
- **Name**: `Clearinghouse-to-Health Plan-275-Receive`
- **Host Partner**: Health Plan Backend ({config.payerId}) 
- **Guest Partner**: Clearinghouse (030240928)
- **Direction**: Receive (Inbound)
- **Message Type**: 275 (Attachment Request)

#### 2️⃣ X12 277 Send Agreement  
- **Name**: `Health Plan-to-Clearinghouse-277-Send`
- **Host Partner**: Health Plan Backend ({config.payerId})
- **Guest Partner**: Clearinghouse (030240928) 
- **Direction**: Send (Outbound)
- **Message Type**: 277 (Status Response)

## 🧪 Testing Plan

### Phase 1: Manual Portal Testing ⏳
1. **Create X12 Agreements** (Azure Portal - Integration Account)
2. **Test X12 Decode** (Upload test 275 file) 
3. **Test X12 Encode** (Create test 277 response)
4. **Verify Trading Partner Mapping**

### Phase 2: Logic Apps Workflow Testing ⏳  
1. **Deploy Workflows** to Logic Apps (if not done)
2. **Configure Connections** (SFTP, Service Bus, Storage)
3. **Test End-to-End Flow**:
   - SFTP → X12 Decode → Data Lake → Service Bus
   - Service Bus → X12 Encode → Outbound Transmission

### Phase 3: Integration Testing ⏳
1. **claims backend API Integration**
2. **Clearinghouse Endpoint Configuration** 
3. **HIPAA Compliance Validation**
4. **Error Handling & Monitoring**

## 🔧 Immediate Next Steps

### Step 1: Create X12 Agreements (PRIORITY)
```
Portal: https://portal.azure.com
Navigate: Resource Groups → rg-hipaa-logic-apps → hipaa-attachments-ia → Agreements
Action: Create both 275 Receive and 277 Send agreements
```

### Step 2: Configure Logic Apps Connections
The workflows need these connections configured:
- **SFTP-SSH**: For inbound file monitoring
- **Azure Blob**: For Data Lake storage  
- **Service Bus**: For message queuing
- **Integration Account**: For X12 processing (should auto-configure)

### Step 3: Test Message Flow
```
Test Sequence:
1. Place test-x12-275-clearinghouse-inbound.edi in SFTP folder
2. Monitor Logic Apps run history
3. Verify X12 decode with trading partners
4. Check Data Lake storage
5. Confirm Service Bus message
6. Test 277 response generation
```

## 💡 Key Validation Points

### X12 Message Validation:
- ✅ **ISA06**: 030240928 (Clearinghouse Sender)
- ✅ **ISA08**: {config.payerId} (Health Plan Receiver)  
- ✅ **GS02**: 030240928 (Clearinghouse Application Sender)
- ✅ **GS03**: {config.payerId} (Health Plan Application Receiver)
- ✅ **ST01**: 275 (Transaction Type)
- ✅ **BHT**: Attachment request header

### Trading Partner Mapping:
- ✅ **Inbound 275**: Clearinghouse (030240928) → Health Plan Backend ({config.payerId})
- ✅ **Outbound 277**: Health Plan Backend ({config.payerId}) → Clearinghouse (030240928)
- ✅ **Qualifier**: ZZ (Mutually Defined) for both partners

## 🌐 Monitoring & Troubleshooting

### Azure Portal Links:
- **Logic Apps**: https://portal.azure.com/#@/resource/.../hipaa-attachments-la
- **Integration Account**: https://portal.azure.com/#@/resource/.../hipaa-attachments-ia  
- **Service Bus**: https://portal.azure.com/#@/resource/.../hipaa-attachments-svc

### Key Metrics to Monitor:
- Logic Apps run history and success rates
- X12 decode/encode operation status  
- Service Bus message counts and processing
- Data Lake file storage and organization
- Integration Account agreement usage

---

## ✅ READY FOR X12 AGREEMENTS CREATION

**Status**: Trading partners configured, infrastructure deployed, test data prepared  
**Next Action**: Create X12 agreements in Azure Portal Integration Account  
**Expected Result**: Fully functional HIPAA 275/277 EDI processing workflow
