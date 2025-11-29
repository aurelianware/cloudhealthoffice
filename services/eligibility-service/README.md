# Cloud Health Office - Eligibility Service

A dual-interface healthcare eligibility verification service supporting both X12 270/271 EDI and FHIR R4 CoverageEligibilityRequest/Response formats.

## Features

- **Dual Interface**: X12 270/271 EDI endpoint AND FHIR CoverageEligibilityRequest
- **Cosmos DB Cache**: Dedicated `eligibility-db` database with TTL for automatic cache expiration
- **Event Grid Publishing**: Publishes "EligibilityChecked" events for downstream processing
- **Azure Container Apps**: Production-ready deployment with Dapr integration
- **Helm Chart**: Kubernetes deployment support
- **Migration Script**: Import existing QNXT eligibility rules from CSV

## Architecture

```
┌─────────────────────────────────────────────────────────────────┐
│                    Eligibility Service                           │
│                                                                  │
│  ┌──────────────────┐     ┌──────────────────┐                  │
│  │  X12 270/271     │     │  FHIR Coverage   │                  │
│  │  Endpoint        │     │  Eligibility     │                  │
│  └────────┬─────────┘     └────────┬─────────┘                  │
│           │                        │                             │
│           └────────────┬───────────┘                             │
│                        ▼                                         │
│             ┌──────────────────────┐                             │
│             │  Eligibility Service │                             │
│             │  (Unified Processing)│                             │
│             └──────────┬───────────┘                             │
│                        │                                         │
│     ┌──────────────────┼──────────────────┐                     │
│     ▼                  ▼                  ▼                      │
│  ┌──────┐        ┌──────────┐       ┌───────────┐               │
│  │Cache │        │Rules     │       │Event Grid │               │
│  │Lookup│        │Engine    │       │Publisher  │               │
│  └──┬───┘        └────┬─────┘       └─────┬─────┘               │
│     │                 │                   │                      │
└─────┼─────────────────┼───────────────────┼──────────────────────┘
      │                 │                   │
      ▼                 ▼                   ▼
┌──────────┐    ┌─────────────┐    ┌─────────────┐
│Cosmos DB │    │QNXT/Backend │    │Event Grid   │
│eligibility│    │Systems      │    │Topic        │
│-db       │    └─────────────┘    └─────────────┘
└──────────┘
```

## API Endpoints

### X12 270/271 Eligibility

```
POST /api/eligibility/x12
Content-Type: application/json (or application/x12)
Accept: application/json (or application/x12)
```

**Request (JSON format):**
```json
{
  "transactionControlNumber": "123456789",
  "transactionDate": "20240115",
  "informationSource": {
    "entityIdentifier": "PR",
    "entityType": "2",
    "name": "ABC Health Plan",
    "identificationCode": "ABCHEALTH",
    "identificationCodeQualifier": "PI"
  },
  "informationReceiver": {
    "entityIdentifier": "1P",
    "entityType": "1",
    "npi": "1234567890"
  },
  "subscriber": {
    "memberId": "MEM123456",
    "firstName": "John",
    "lastName": "Doe",
    "dateOfBirth": "19850615",
    "gender": "M"
  },
  "serviceTypeCodes": ["30", "48"]
}
```

**Response:**
```json
{
  "format": "X12",
  "x12Response": {
    "transactionControlNumber": "123456789",
    "eligibilityStatus": "active",
    "subscriber": {
      "memberId": "MEM123456",
      "firstName": "John",
      "lastName": "Doe",
      "planName": "Gold PPO Plan"
    },
    "benefits": [
      {
        "serviceTypeCode": "30",
        "serviceTypeDescription": "Health Benefit Plan Coverage",
        "eligibilityInfoCode": "1",
        "inNetwork": true
      }
    ]
  },
  "fromCache": false,
  "responseTimeMs": 125
}
```

### FHIR CoverageEligibilityRequest

```
POST /api/eligibility/fhir
POST /fhir/CoverageEligibilityRequest
Content-Type: application/fhir+json
```

**Request:**
```json
{
  "resourceType": "CoverageEligibilityRequest",
  "status": "active",
  "purpose": ["validation", "benefits"],
  "patient": {
    "reference": "Patient/MEM123456",
    "identifier": {
      "value": "MEM123456"
    }
  },
  "created": "2024-01-15",
  "insurer": {
    "reference": "Organization/ABCHEALTH"
  },
  "item": [
    {
      "category": {
        "coding": [{
          "system": "https://x12.org/codes/service-type-codes",
          "code": "30"
        }]
      }
    }
  ]
}
```

**Response:**
```json
{
  "resourceType": "CoverageEligibilityResponse",
  "status": "active",
  "purpose": ["validation", "benefits"],
  "patient": {
    "reference": "Patient/MEM123456"
  },
  "created": "2024-01-15T10:30:00Z",
  "outcome": "complete",
  "insurance": [{
    "coverage": {
      "reference": "Coverage/MEM123456"
    },
    "inforce": true,
    "item": [{
      "category": {
        "coding": [{
          "code": "30",
          "display": "Health Benefit Plan Coverage"
        }]
      },
      "excluded": false,
      "authorizationRequired": false
    }]
  }]
}
```

### Unified Eligibility Endpoint

```
POST /api/eligibility
Content-Type: application/json
```

Accepts both X12 and FHIR formats with a `format` field.

### Health Check

```
GET /health
GET /healthz (liveness)
GET /readyz (readiness)
```

## Deployment

### Azure Container Apps (Recommended)

```bash
# Deploy using Bicep
az deployment group create \
  --resource-group your-rg \
  --template-file infra/main.bicep \
  --parameters baseName=your-prefix
```

### Kubernetes with Helm

```bash
# Add any required secret references first
kubectl create secret generic cosmos-credentials \
  --from-literal=endpoint=https://your-cosmos.documents.azure.com:443/ \
  --from-literal=key=your-cosmos-key

# Install the Helm chart
helm install eligibility-service ./charts/eligibility-service \
  --set existingSecrets.cosmos=cosmos-credentials \
  --set config.dapr.enabled=true
```

### Docker

```bash
# Build the image
docker build -t eligibility-service:latest .

# Run locally
docker run -p 3000:3000 \
  -e COSMOS_ENDPOINT=https://localhost:8081 \
  -e COSMOS_DATABASE=eligibility-db \
  eligibility-service:latest
```

## Migration from QNXT

### Generate Sample CSV Template

```bash
npm run migrate:sample
# Creates sample-eligibility-rules.csv
```

### Import Rules from CSV

```bash
npm run migrate:import -- qnxt-rules.csv --output rules.json --validate
```

### CSV Format

The migration script expects CSV with these columns:

| Column | Description | Required |
|--------|-------------|----------|
| rule_id | Unique rule identifier | Yes |
| rule_name | Human-readable name | Yes |
| plan_code | Plan identifier | Yes |
| service_type_code | X12 service type | Yes |
| benefit_category | Category name | Yes |
| coverage_indicator | covered/not_covered/limited/excluded | Yes |
| prior_auth_required | true/false | Yes |
| referral_required | true/false | Yes |
| in_network_copay | Dollar amount | No |
| in_network_coinsurance | Percentage (0-100) | No |
| out_network_copay | Dollar amount | No |
| out_network_coinsurance | Percentage (0-100) | No |
| effective_start_date | YYYYMMDD or YYYY-MM-DD | Yes |
| effective_end_date | YYYYMMDD or YYYY-MM-DD | No |
| priority | Integer (lower = higher priority) | Yes |
| is_active | true/false | Yes |

## Configuration

### Environment Variables

| Variable | Description | Default |
|----------|-------------|---------|
| `PORT` | Server port | 3000 |
| `COSMOS_ENDPOINT` | Cosmos DB endpoint | Required |
| `COSMOS_DATABASE` | Database name | eligibility-db |
| `COSMOS_CONTAINER` | Container name | eligibility-cache |
| `EVENT_GRID_ENDPOINT` | Event Grid topic endpoint | Optional |
| `CACHE_ENABLED` | Enable caching | true |
| `CACHE_ACTIVE_TTL` | TTL for active members (seconds) | 86400 |
| `CACHE_INACTIVE_TTL` | TTL for inactive members (seconds) | 3600 |
| `CACHE_MAX_AGE` | Max cache age before refresh (seconds) | 43200 |
| `DAPR_ENABLED` | Enable Dapr sidecar | false |
| `DAPR_APP_ID` | Dapr application ID | eligibility-service |
| `QNXT_BASE_URL` | QNXT API base URL | Optional |
| `QNXT_RULES_FILE` | Path to eligibility rules CSV | Optional |

## Event Grid Events

### EligibilityChecked Event

Published after each eligibility check:

```json
{
  "id": "uuid",
  "eventType": "EligibilityChecked",
  "subject": "MEM123456",
  "eventTime": "2024-01-15T10:30:00Z",
  "dataVersion": "1.0",
  "data": {
    "memberId": "MEM123456",
    "payerId": "ABCHEALTH",
    "providerNpi": "1234567890",
    "requestType": "X12_270",
    "eligibilityStatus": "active",
    "serviceDate": "20240115",
    "serviceTypeCodes": ["30", "48"],
    "fromCache": false,
    "responseTimeMs": 125
  }
}
```

## Cosmos DB Structure

### Database: eligibility-db

| Container | Partition Key | TTL | Purpose |
|-----------|--------------|-----|---------|
| eligibility-cache | /memberId | Configurable | Eligibility response cache |
| eligibility-rules | /planCode | None | QNXT eligibility rules |
| dapr-state | /partitionKey | None | Dapr state store |

## Development

```bash
# Install dependencies
npm install

# Run in development mode
npm run dev

# Run tests
npm test

# Build for production
npm run build

# Start production server
npm start
```

## Compliance

- **HIPAA**: All PHI is encrypted at rest and in transit
- **CMS Interoperability**: Supports CMS-9115-F Patient Access requirements
- **X12**: Compliant with HIPAA X12 005010X279A1
- **FHIR**: Supports HL7 FHIR R4 and Da Vinci PDex Implementation Guide

## License

Apache-2.0
