# Cloud Health Office - Claims Scrubbing Service

Pre-adjudication claims validation service for 837P (Professional), 837I (Institutional), and 837D (Dental) claims. Improves first-pass rates by validating claims against configurable rules before adjudication.

## Features

- **Multi-Transaction Support**: Validates 837P, 837I, and 837D claims
- **Configurable Rule Engine**: Standard rules plus custom/payer-specific rules
- **First-Pass Rate Optimization**: Catches invalid claims before adjudication
- **Intelligent Routing**: Clean claims → adjudication, flagged claims → work queues
- **Comprehensive Validation**:
  - Data completeness checks
  - Code validation (ICD-10, CPT, HCPCS, Revenue)
  - Date logic validation
  - Amount verification
  - NPI validation (Luhn algorithm)
  - Modifier validation
- **Kafka Integration**: Consistent with repository messaging patterns
- **Azure Integration**: Cosmos DB, Blob Storage
- **Dapr Support**: Optional sidecar integration

## Quick Start

```bash
# Install dependencies
npm install

# Build
npm run build

# Run tests
npm test

# Start service (development)
npm run dev

# Start service (production)
npm start
```

## API Endpoints

### Validate Single Claim

```http
POST /api/claims/validate
Content-Type: application/json

{
  "claim": {
    "claimId": "CLM-001",
    "claimType": "837P",
    "billingProvider": { ... },
    "subscriber": { ... },
    "serviceLines": [ ... ]
  },
  "autoCorrect": false,
  "correlationId": "optional-correlation-id"
}
```

### Validate Batch

```http
POST /api/claims/validate/batch
Content-Type: application/json

{
  "claims": [ ... ],
  "correlationId": "optional-correlation-id"
}
```

### Get Validation Rules

```http
GET /api/rules
```

### Get Rules by Category

```http
GET /api/rules/category/data-completeness
```

### Health Check

```http
GET /health
GET /healthz
GET /readyz
```

## Validation Rules

### Standard Rules (Pre-configured)

| Category | Rules |
|----------|-------|
| Data Completeness | Member ID, DOB, NPI, Diagnosis codes, Service lines |
| Code Validation | ICD-10, CPT, HCPCS, Revenue codes, Place of service |
| Date Logic | Future dates, Filing limits, Admission/discharge |
| Amount Logic | Positive charges, Total matching, Units validation |
| Provider Validation | NPI format (Luhn), Tax ID format |
| Modifier Validation | Format, duplicates, ordering |

### Rule Severities

- **Error**: Claim fails validation, routed to work queue
- **Warning**: Claim flagged for review but may proceed
- **Info**: Informational, no routing impact

## Configuration

### Environment Variables

| Variable | Description | Default |
|----------|-------------|---------|
| `PORT` | HTTP server port | 3000 |
| `KAFKA_BOOTSTRAP_SERVERS` | Kafka bootstrap servers | localhost:9092 |
| `KAFKA_CLIENT_ID` | Kafka client ID | claims-scrubber |
| `KAFKA_CONSUMER_GROUP` | Kafka consumer group | claims-scrubber-group |
| `KAFKA_SASL_USERNAME` | SASL username (optional) | - |
| `KAFKA_SASL_PASSWORD` | SASL password (optional) | - |
| `KAFKA_SASL_MECHANISM` | SASL mechanism | scram-sha-512 |
| `KAFKA_SSL` | Enable SSL/TLS | false |
| `INBOUND_CLAIMS_TOPIC` | Topic for incoming claims | claims-inbound |
| `CLEAN_CLAIMS_TOPIC` | Topic for validated claims | claims-adjudication |
| `FLAGGED_CLAIMS_TOPIC` | Topic for flagged claims | claims-work-queue |
| `REJECTED_CLAIMS_TOPIC` | Topic for rejected claims | claims-rejected |
| `COSMOS_ENDPOINT` | Cosmos DB endpoint | - |
| `COSMOS_DATABASE` | Database name | claims-scrubbing |
| `STORAGE_ACCOUNT_NAME` | Storage account for archival | - |
| `PARALLEL_RULES` | Enable parallel rule execution | false |
| `FIRST_PASS_RATE_TARGET` | Target first-pass rate (%) | 95 |

## Claim Types

### 837P - Professional Claims

Office visits, outpatient services, provider-rendered services.

**Key Fields**:
- Place of Service Code
- CPT/HCPCS Procedure Codes
- Modifiers

### 837I - Institutional Claims

Hospital inpatient/outpatient, facility claims.

**Key Fields**:
- Facility Type Code
- Revenue Codes
- Admission/Discharge Dates
- DRG Code

### 837D - Dental Claims

Dental procedures and services.

**Key Fields**:
- ADA Procedure Codes
- Tooth Information
- Oral Cavity Designations

## Routing Logic

```
Claim Submitted
     │
     ▼
┌────────────────┐
│ Run Validation │
│     Rules      │
└───────┬────────┘
        │
   ┌────┴────┐
   ▼         ▼
┌──────┐  ┌───────┐
│Errors│  │No Errs│
│ > 0  │  │       │
└──┬───┘  └───┬───┘
   │          │
   ▼          ▼
┌──────────┐  ┌─────────────┐
│Work Queue│  │ Warnings?   │
│(Errors)  │  └──────┬──────┘
└──────────┘         │
               ┌─────┴─────┐
               ▼           ▼
          ┌────────┐  ┌──────────┐
          │Warnings│  │   Clean  │
          │  > 0   │  │          │
          └───┬────┘  └────┬─────┘
              │            │
              ▼            ▼
         ┌─────────┐  ┌───────────┐
         │ Work    │  │Adjudication│
         │ Queue   │  │  System   │
         │(Warning)│  └───────────┘
         └─────────┘
```

## Custom Rules

Add payer-specific or custom validation rules:

```typescript
const customRule: CustomRule = {
  ruleId: 'PAYER001-AUTH',
  ruleName: 'Prior Auth Required for MRI',
  description: 'MRI procedures require prior authorization',
  category: 'authorization',
  severity: 'error',
  appliesTo: ['837P', '837I'],
  enabled: true,
  priority: 50,
  type: 'custom',
  payerId: 'PAYER001',
  validationScript: `
    // Custom validation logic
    const mriCodes = ['70551', '70552', '70553'];
    const hasMri = claim.serviceLines.some(l => mriCodes.includes(l.procedureCode));
    const hasAuth = !!claim.claimHeader.priorAuthorizationNumber;
    return hasMri && !hasAuth ? { passed: false, message: 'MRI requires prior auth' } : { passed: true };
  `
};

await service.addCustomRule(customRule);
```

## Metrics

The service tracks key performance metrics:

- `claimsProcessed` - Total claims validated
- `claimsClean` - Claims passing validation
- `claimsFlagged` - Claims with warnings
- `claimsRejected` - Claims with errors
- `averageValidationTimeMs` - Average processing time
- `firstPassRate` - Percentage of clean claims

Access metrics via:
```http
GET /metrics
GET /api/metrics
```

## Docker

```bash
# Build image
docker build -t claims-scrubber:latest .

# Run container
docker run -p 3000:3000 \
  -e COSMOS_ENDPOINT=https://... \
  -e KAFKA_BOOTSTRAP_SERVERS=broker1:9092,broker2:9092 \
  claims-scrubber:latest
```

## Kubernetes Deployment

See `charts/` directory for Helm chart or use the provided Argo Workflow.

## Testing

```bash
# Run all tests
npm test

# Run with coverage
npm run test:coverage

# Run specific test file
npm test -- tests/claims-scrubber.test.ts
```

## Related Documentation

- [Architecture Overview](../../ARCHITECTURE.md)
- [Eligibility Service](../eligibility-service/README.md)
- [Argo Workflows](../../argo-workflows/)
