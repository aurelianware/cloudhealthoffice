# ClaimRiskScorer Azure Function

Azure Function (Python + PyTorch) that scores healthcare claims for fraud/abuse risk.

## Overview

This function:
1. **Triggers** on every inbound 837 claim via Service Bus topic `edi-837-claims`
2. **Scores** fraud/abuse risk (0-100) using a PyTorch model
3. **Generates** custom ZZZ segment for 277 response with score and top 3 reasons
4. **Logs** high-risk claims (score >= 80) to Application Insights as custom event "HighRiskClaim"

## Architecture

```
Service Bus (edi-837-claims topic)
        │
        ▼
┌───────────────────────┐
│   ClaimRiskScorer     │
│   Azure Function      │
│                       │
│  ┌─────────────────┐  │
│  │ claim_parser.py │──┼──► Parse 837 EDI
│  └────────┬────────┘  │
│           │           │
│  ┌────────▼────────┐  │
│  │    model.py     │──┼──► Score with PyTorch/Rules
│  └────────┬────────┘  │
│           │           │
│  ┌────────▼────────┐  │
│  │ zzz_segment.py  │──┼──► Generate ZZZ segment
│  └─────────────────┘  │
└───────────┬───────────┘
            │
            ▼
    Application Insights
    (HighRiskClaim events)
```

## Risk Scoring

### Score Ranges
- **0-30**: Low risk - routine processing
- **31-60**: Medium risk - standard review
- **61-80**: High risk - enhanced review
- **81-100**: Critical risk - triggers HighRiskClaim event

### Risk Reasons
The model identifies these risk factors:
- `HIGH_BILL_AMOUNT` - Unusually high billed amount
- `PROVIDER_HISTORY` - Provider has fraud history flags
- `PROCEDURE_MISMATCH` - Procedure doesn't match diagnosis
- `DUPLICATE_PATTERN` - Potential duplicate claim pattern
- `UNBUNDLING` - Possible code unbundling detected
- `UPCODING` - Potential upcoding detected
- `OUT_OF_NETWORK` - Out-of-network provider flagged
- `NEW_MEMBER` - Recent enrollment with high-cost claims

## ZZZ Segment Format

The custom ZZZ segment is added to 277 responses:

```
ZZZ*RS*75.5*HI*HIGH_BILL_AMOUNT*Unusually high billed amount*PROVIDER_HISTORY*Provider risk indicators*UNBUNDLING*Multiple modifiers detected~
```

Fields:
- ZZZ01: Qualifier ("RS" = Risk Score)
- ZZZ02: Risk score (0-100)
- ZZZ03: Category (LO/MD/HI/CR)
- ZZZ04-05: Reason 1 (code + description)
- ZZZ06-07: Reason 2 (code + description)
- ZZZ08-09: Reason 3 (code + description)

## Configuration

### Environment Variables

| Variable | Description | Required |
|----------|-------------|----------|
| `ServiceBusConnection` | Service Bus connection string | Yes |
| `APPINSIGHTS_INSTRUMENTATIONKEY` | App Insights instrumentation key | No |
| `APPLICATIONINSIGHTS_CONNECTION_STRING` | App Insights connection string | No |
| `MODEL_PATH` | Path to PyTorch model file | No (default: /ml/claim-fraud-v1.pt) |

### Local Development

1. Copy `local.settings.json.example` to `local.settings.json`
2. Fill in your Azure Service Bus and App Insights credentials
3. Install dependencies:
   ```bash
   pip install -r requirements.txt
   ```
4. Run with Azure Functions Core Tools:
   ```bash
   func start
   ```

## HIPAA Compliance

This function is designed for HIPAA compliance:

- **No PHI in Logs**: Application Insights events contain only anonymized/aggregate data
- **No PHI in Model**: The PyTorch model uses only derived features, not raw PHI
- **Secure Transport**: All data flows through encrypted Service Bus connections
- **Minimal Data**: Only risk-relevant features are extracted from claims

## Testing

Run unit tests:
```bash
cd functions/ClaimRiskScorer
python -m pytest tests/ -v
```

## Deployment

The function is deployed as part of the Cloud Health Office infrastructure via Bicep:

```bash
az deployment group create \
  -g <resource-group> \
  -f infra/main.bicep \
  -p baseName=<base-name>
```

## Dependencies

- Python 3.11
- Azure Functions runtime v4
- PyTorch (CPU-only)
- Azure Service Bus SDK
- Application Insights SDK
