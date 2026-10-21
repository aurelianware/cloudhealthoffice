# CMS-0057-F Compliance Quickstart

**A running CMS-0057-F sandbox in 15 minutes** ⏱️

This guide stands up Cloud Health Office's CMS-0057-F FHIR R4 APIs with synthetic data and runs the acceptance suite against them. It shows how the platform behaves; your plan's compliance depends on your own data, configuration and operations.

## What is CMS-0057-F?

The **CMS Interoperability and Prior Authorization Final Rule (CMS-0057-F)** requires health plans to:

1. **Patient Access API** - Let patients access their data via FHIR (effective Jan 1, 2026)
2. **Provider Access API** - Give providers access to member data (effective Jan 1, 2027)
3. **Payer-to-Payer API** - Exchange member data when patients switch plans (effective Jan 1, 2027)
4. **Prior Authorization API** - Convert prior auth to FHIR-based workflow (effective Jan 1, 2027)

## Prerequisites

- Docker installed
- Node.js 18+ (for testing)
- Health plan with X12 EDI infrastructure
- Basic understanding of FHIR R4

## Quick Start

### 1. Deploy Cloud Health Office (5 minutes)

```bash
# Clone the repository
git clone https://github.com/aurelianware/cloudhealthoffice.git
cd cloudhealthoffice

# Install dependencies
npm install

# Start local FHIR server
npm run start:fhir-server

# Verify server is running
curl http://localhost:3000/fhir/r4/metadata
```

Expected output: FHIR CapabilityStatement JSON

### 2. Test Patient Access API (5 minutes)

Using synthetic test data:

```bash
# Set test patient ID
export PATIENT_ID="PAT001"

# Test 1: Retrieve patient demographics
curl -H "Authorization: Bearer test-token" \
  http://localhost:3000/fhir/r4/Patient/${PATIENT_ID}

# Test 2: Get claims history  
curl -H "Authorization: Bearer test-token" \
  "http://localhost:3000/fhir/r4/Claim?patient=${PATIENT_ID}"

# Test 3: Get explanation of benefits
curl -H "Authorization: Bearer test-token" \
  "http://localhost:3000/fhir/r4/ExplanationOfBenefit?patient=${PATIENT_ID}"

# Test 4: Get complete patient record ($everything)
curl -H "Authorization: Bearer test-token" \
  "http://localhost:3000/fhir/r4/Patient/${PATIENT_ID}/\$everything"
```

✅ **Pass:** All endpoints return FHIR R4 JSON  
❌ **Fail:** 404 or non-FHIR responses

### 3. Run the CMS-0057-F Acceptance Suite (3 minutes)

```bash
# Executable acceptance scenarios for the four CMS-0057-F APIs (synthetic data)
dotnet test tests/Cms0057Acceptance.Tests/Cms0057Acceptance.Tests.csproj -c Debug
```

✅ **Pass:** every scenario passes. A green run shows the scenarios in the suite
behave as specified against synthetic data. It is not a CMS certification (CMS does
not certify payer APIs), a HIPAA attestation or a production-readiness review.
Reconciled capability status is in the
[capability ledger](../../docs/CAPABILITY_RECONCILIATION.md).

### 4. View Interactive API Documentation (2 minutes)

Open in browser:

- **Patient Access API**: https://petstore.swagger.io/?url=https://raw.githubusercontent.com/aurelianware/cloudhealthoffice/main/api/openapi/patient-access-api.yaml
- **Provider Access API**: https://petstore.swagger.io/?url=https://raw.githubusercontent.com/aurelianware/cloudhealthoffice/main/api/openapi/provider-access-api.yaml
- **Payer-to-Payer API**: https://petstore.swagger.io/?url=https://raw.githubusercontent.com/aurelianware/cloudhealthoffice/main/api/openapi/payer-to-payer-api.yaml
- **Prior Auth API**: https://petstore.swagger.io/?url=https://raw.githubusercontent.com/aurelianware/cloudhealthoffice/main/api/openapi/prior-auth-api.yaml

## What's Covered

| Requirement | Implementation | Status |
|-------------|----------------|--------|
| Patient Access API | FHIR R4 endpoints with OAuth 2.0 | ✅ Production |
| Provider Access API | SMART on FHIR + bulk member match | ✅ Production |
| Payer-to-Payer API | $member-match + $everything | ✅ Production |
| Prior Authorization | X12 278 ↔ FHIR bidirectional | ✅ Production |
| US Core Profiles | Patient, Claim, EOB, Encounter | ✅ Validated |
| OAuth 2.0 Security | Azure AD integration | ✅ Configured |
| HIPAA Audit Logs | All access logged | ✅ Required |
| Bulk Data Export | NDJSON format | ✅ Supported |

## Integration Workflow

### X12 EDI → FHIR Translation

Cloud Health Office automatically converts your existing X12 transactions to FHIR:

```
X12 837 (Claims) → FHIR Claim resource
X12 835 (Remittance) → FHIR ExplanationOfBenefit
X12 270 (Eligibility Inquiry) → FHIR CoverageEligibilityRequest
X12 271 (Eligibility Response) → FHIR CoverageEligibilityResponse
X12 278 (Prior Auth) → FHIR ServiceRequest + Task
```

No need to rebuild your core systems - just add FHIR API layer on top!

## Production Deployment

### Azure (Recommended)

```bash
# Deploy to Azure
cd infrastructure/azure
az deployment group create \
  --resource-group rg-cloudhealthoffice \
  --template-file main.bicep \
  --parameters @main.parameters.json

# Configure OAuth
./scripts/setup-portal-azuread-secret.sh
```

### Kubernetes

```bash
# Deploy with Helm
cd helm/cloudhealthoffice
helm install cho . --namespace healthcare --create-namespace

# Verify deployment
kubectl get pods -n healthcare
```

### AWS

```bash
# Deploy with CDK
cd infrastructure/aws
npm install
cdk deploy
```

## Testing with Real Data

### Connect Your Test Payer

1. **Add payer configuration:**
```bash
cp config/payer-config.example.json config/my-payer.json
# Edit my-payer.json with your details
```

2. **Load test claims:**
```bash
npm run seed:claims -- --payer my-payer --count 100
```

3. **Verify FHIR conversion:**
```bash
curl -H "Authorization: Bearer ${TOKEN}" \
  "http://localhost:3000/fhir/r4/Claim?_count=10"
```

## Compliance Evidence Support

CMS does not certify payer APIs; each payer demonstrates its own compliance. To build
that evidence with Cloud Health Office:

1. **Generate the acceptance evidence package:** the
   [CMS-0057-F Acceptance Evidence workflow](../../.github/workflows/cms0057-acceptance-evidence.yml)
   runs the acceptance suite and produces a sanitized evidence snapshot.

2. **Review the pilot checklist:** See
   [`docs/compliance/CMS-0057-F-PILOT-DILIGENCE-CHECKLIST.md`](../../docs/compliance/CMS-0057-F-PILOT-DILIGENCE-CHECKLIST.md)
   and the [readiness matrix](../../docs/compliance/CMS-0057-F-READINESS-MATRIX.md).

3. **Get support:** Email compliance@cloudhealthoffice.com

## Common Issues

### "OAuth token invalid"
**Solution:** Configure Azure AD app registration:
```bash
./scripts/setup-portal-azuread-secret.sh
```

### "X12 mapping failed"
**Solution:** Verify X12 schema version matches your clearinghouse:
```bash
npm run validate:x12-schemas
```

### "FHIR validation errors"
**Solution:** Enable US Core profile validation:
```json
{
  "fhir": {
    "validateProfiles": true,
    "strictMode": true
  }
}
```

## Next Steps

- ✅ **Complete:** Sandbox running and acceptance suite passing
- 📋 **Next:** [Patient Access Detailed Guide](./patient-access-quickstart.md)
- 📋 **Next:** [Provider Access Setup](./provider-access-quickstart.md)
- 📋 **Next:** [Prior Authorization Integration](./prior-auth-quickstart.md)
- 📋 **Advanced:** [Custom Rule Engine](./claims-scrubbing-quickstart.md)

## Resources

- **Full Documentation:** [docs/features/FHIR-INTEGRATION.md](../../docs/features/FHIR-INTEGRATION.md)
- **OpenAPI Specs:** [api/openapi/](../openapi/)
- **CMS Final Rule:** [CMS-0057-F PDF](https://www.federalregister.gov/documents/2024/02/08/2024-01822/medicare-and-medicaid-programs-patient-protection-and-affordable-care-act-interoperability-and)
- **Da Vinci PDex:** [hl7.org/fhir/us/davinci-pdex](http://hl7.org/fhir/us/davinci-pdex/)

## Support

- 🐛 **Issues:** [GitHub Issues](https://github.com/aurelianware/cloudhealthoffice/issues)
- 💬 **Discussions:** [GitHub Discussions](https://github.com/aurelianware/cloudhealthoffice/discussions)
- 📧 **Email:** support@cloudhealthoffice.com
- 📅 **Office Hours:** Tuesdays 2-3pm ET ([Book a call](https://calendly.com/cloudhealthoffice))

---

**Time to a running sandbox: 15 minutes** ✅  
**Cost to implement: $0 for non-production use (BSL 1.1)** 💰
