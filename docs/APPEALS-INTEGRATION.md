# Appeals Module Integration with Enhanced Claim Status

## Overview

The Appeals module (PR #49) integrates seamlessly with Enhanced Claim Status (ECS) to enable one-click appeal workflows directly from claim status queries. This integration leverages the ValueAdds277 `eligibleForAppeal` flag and appeal metadata to provide providers with a streamlined dispute process.

## Architecture

```
┌──────────────┐      ┌──────────────┐      ┌──────────────┐
│   Provider   │ ───> │ ECS Query    │ ───> │  QNXT API    │
│   Portal     │ <─── │  (Logic App) │ <─── │              │
└──────────────┘      └──────────────┘      └──────────────┘
       │                      │
       │                      │ ValueAdds277 Response
       │                      │ - eligibleForAppeal: true
       │                      │ - appealMessage
       │                      │ - appealTimelyFilingDate
       │                      v
       │              ┌──────────────┐
       └───────────>  │   Appeals    │
          Initiate    │   Module     │
          Appeal      │  (Logic App) │
                      └──────────────┘
                              │
                              v
                      ┌──────────────┐
                      │ Service Bus  │
                      │ (appeals-in) │
                      └──────────────┘
```

## ECS Integration Points

### 1. eligibleForAppeal Flag

The `eligibleForAppeal` flag is calculated by the ECS workflow based on claim status:

```javascript
eligibleForAppeal = (claimStatus === 'Denied' || claimStatus === 'Partially Paid')
```

**When true:**
- Provider UI displays "Dispute Claim" button
- Appeal metadata is populated
- Provider can initiate appeal workflow with one click

**When false:**
- "Dispute Claim" button is disabled/hidden
- Appeal is not available for this claim status

### 2. Appeal Metadata Fields

Three metadata fields support the appeal workflow:

| Field | Type | Description | Example |
|-------|------|-------------|---------|
| `appealMessage` | string | Provider-facing message with filing deadline | "This claim may be eligible for appeal. Timely filing deadline: 2024-07-15" |
| `appealType` | string | Type of appeal allowed | "Reconsideration" |
| `appealTimelyFilingDate` | date | Last date to file appeal (CCYYMMDD) | "20240715" |

**Appeal Type Values:**
- `Reconsideration`: Standard first-level appeal
- `Redetermination`: Medicare/Medicaid specific
- `Administrative Review`: Higher-level review

**Timely Filing Calculation:**
```javascript
appealTimelyFilingDate = finalizedDate + timelyFilingDays (default: 180 days)
```

### 3. Claim Status Eligibility Matrix

| Claim Status | eligibleForAppeal | Reasoning |
|--------------|-------------------|-----------|
| Paid | false | Claim is finalized and paid in full |
| Partially Paid | **true** | Provider may dispute underpaid amount |
| Denied | **true** | Provider may appeal denial decision |
| Pending | false | Claim is still being processed |
| In Process | false | Claim is actively being reviewed |
| Suspended | false | Claim processing is temporarily paused |
| Forwarded | false | Claim forwarded to another payer |
| Adjusted | false | Claim already adjusted, use corrections |

## Complete Provider Workflow

### Scenario: Denied Claim Appeal

**Step 1: Provider Queries Claim**

Request:
```json
{
  "searchMethod": "ClaimHistory",
  "requestId": "REQ-2024-APPEAL-001",
  "submitterId": "PROV-12345",
  "claimHistorySearch": {
    "claimNumber": "CLM987654321"
  }
}
```

**Step 2: ECS Returns ValueAdds277 Response**

Response (excerpt):
```json
{
  "claims": [
    {
      "claimNumber": "CLM987654321",
      "claimStatus": "Denied",
      "statusCode": "P4",
      "statusCodeDescription": "The procedure code is inconsistent with the modifier used",
      
      "BILLED": 450.00,
      "ALLOWED": 0.00,
      "INSURANCE_TOTAL_PAID": 0.00,
      
      "reasonCodes": ["CO-96", "CO-197"],
      "comment": "Procedure code 29881 with modifier 59 is not appropriate.",
      
      "eligibleForAppeal": true,
      "appealMessage": "This claim may be eligible for appeal. Timely filing deadline: 2024-07-06",
      "appealType": "Reconsideration",
      "appealTimelyFilingDate": "20240706"
    }
  ]
}
```

**Step 3: UI Displays "Dispute Claim" Button**

UI Logic:
```javascript
function shouldShowDisputeButton(claim) {
  return claim.eligibleForAppeal === true;
}

function getAppealDeadline(claim) {
  return claim.appealTimelyFilingDate; // "20240706"
}

function getAppealWarning(claim) {
  const deadline = parseDate(claim.appealTimelyFilingDate);
  const daysRemaining = Math.floor((deadline - new Date()) / (1000 * 60 * 60 * 24));
  
  if (daysRemaining < 30) {
    return `⚠️ Appeal deadline in ${daysRemaining} days!`;
  }
  return claim.appealMessage;
}
```

**Step 4: Provider Clicks "Dispute Claim"**

Appeals module receives pre-populated data:
```json
{
  "appealType": "Reconsideration",
  "claimNumber": "CLM987654321",
  "patientAccountNumber": "PAT-2024-002",
  "memberId": "M789012",
  "providerNpi": "5550100001",
  "claimStatus": "Denied",
  "denialReasons": ["CO-96", "CO-197"],
  "denialComment": "Procedure code 29881 with modifier 59 is not appropriate.",
  "billedAmount": 450.00,
  "timelyFilingDeadline": "20240706",
  "serviceFromDate": "20231215",
  "serviceToDate": "20231215",
  "diagnosisCodes": ["M25.551", "S83.511A"],
  "procedureCode": "29881",
  "modifiers": ["59"]
}
```

**Step 5: Provider Submits Appeal**

Appeal submission includes:
- Dispute reason selected by provider
- Supporting narrative
- Optional attachment references
- Provider contact information

**Step 6: Appeals Workflow Processes**

The Appeals Logic App:
1. Validates appeal data
2. Checks timely filing deadline
3. Generates X12 277 appeal transaction
4. Publishes to Service Bus `appeals-in` topic
5. Updates claim tracking system
6. Sends confirmation to provider

## UI Implementation Examples

### React Component Example

```jsx
import React from 'react';

function ClaimDetailCard({ claim }) {
  const canAppeal = claim.eligibleForAppeal === true;
  const appealDeadline = new Date(
    claim.appealTimelyFilingDate.replace(
      /(\d{4})(\d{2})(\d{2})/,
      '$1-$2-$3'
    )
  );
  const daysUntilDeadline = Math.floor(
    (appealDeadline - new Date()) / (1000 * 60 * 60 * 24)
  );

  const handleDisputeClaim = () => {
    // Navigate to appeals flow with pre-populated data
    window.location.href = `/appeals/new?claimNumber=${claim.claimNumber}`;
  };

  return (
    <div className="claim-card">
      <h3>Claim {claim.claimNumber}</h3>
      <div className="claim-status denied">
        <span className="status-badge">Denied</span>
        <p>{claim.statusCodeDescription}</p>
      </div>

      <div className="financial-summary">
        <div>Billed: ${claim.BILLED.toFixed(2)}</div>
        <div>Allowed: ${claim.ALLOWED.toFixed(2)}</div>
        <div>Paid: ${claim.INSURANCE_TOTAL_PAID.toFixed(2)}</div>
      </div>

      <div className="denial-details">
        <h4>Denial Reason</h4>
        <p>{claim.comment}</p>
        <div className="denial-codes">
          {claim.reasonCodes.map(code => (
            <span key={code} className="code-badge">{code}</span>
          ))}
        </div>
      </div>

      {canAppeal && (
        <div className="appeal-section">
          <div className="appeal-message">
            {daysUntilDeadline < 30 ? (
              <div className="warning">
                ⚠️ Appeal deadline in {daysUntilDeadline} days!
              </div>
            ) : (
              <div className="info">{claim.appealMessage}</div>
            )}
          </div>
          <button 
            className="btn-primary dispute-btn"
            onClick={handleDisputeClaim}
          >
            Dispute Claim
          </button>
        </div>
      )}

      {!canAppeal && (
        <div className="appeal-unavailable">
          <p>This claim is not eligible for appeal at this time.</p>
        </div>
      )}
    </div>
  );
}
```

### Angular Component Example

```typescript
import { Component, Input } from '@angular/core';
import { Router } from '@angular/router';

interface Claim {
  claimNumber: string;
  claimStatus: string;
  eligibleForAppeal: boolean;
  appealMessage: string;
  appealTimelyFilingDate: string;
  BILLED: number;
  ALLOWED: number;
  INSURANCE_TOTAL_PAID: number;
  comment: string;
  reasonCodes: string[];
}

@Component({
  selector: 'app-claim-detail',
  template: `
    <div class="claim-card">
      <h3>Claim {{ claim.claimNumber }}</h3>
      
      <div *ngIf="canAppeal" class="appeal-section">
        <div [ngClass]="appealUrgency">
          {{ appealWarningMessage }}
        </div>
        <button 
          (click)="initiateAppeal()" 
          class="btn-dispute"
        >
          Dispute Claim
        </button>
      </div>
      
      <div *ngIf="!canAppeal" class="appeal-unavailable">
        Appeals are not available for this claim status.
      </div>
    </div>
  `
})
export class ClaimDetailComponent {
  @Input() claim!: Claim;

  get canAppeal(): boolean {
    return this.claim.eligibleForAppeal === true;
  }

  get daysUntilDeadline(): number {
    const deadline = new Date(
      this.claim.appealTimelyFilingDate.replace(
        /(\d{4})(\d{2})(\d{2})/,
        '$1-$2-$3'
      )
    );
    return Math.floor(
      (deadline.getTime() - new Date().getTime()) / (1000 * 60 * 60 * 24)
    );
  }

  get appealUrgency(): string {
    return this.daysUntilDeadline < 30 ? 'warning' : 'info';
  }

  get appealWarningMessage(): string {
    if (this.daysUntilDeadline < 30) {
      return `⚠️ Appeal deadline in ${this.daysUntilDeadline} days!`;
    }
    return this.claim.appealMessage;
  }

  constructor(private router: Router) {}

  initiateAppeal(): void {
    this.router.navigate(['/appeals/new'], {
      queryParams: { claimNumber: this.claim.claimNumber }
    });
  }
}
```

## Configuration

Appeals integration is configured via the Availity integration configuration:

```json
{
  "ecs": {
    "enabled": true,
    "valueAdds277": {
      "enabled": true,
      "integrationFlags": {
        "appeals": true
      },
      "appealConfiguration": {
        "timelyFilingDays": 180,
        "includeAppealMetadata": true,
        "defaultAppealType": "Reconsideration"
      }
    }
  },
  "appeals": {
    "enabled": true,
    "consumesEcsData": true
  }
}
```

**Configuration Location:** `config/schemas/availity-integration-config.schema.json`

### Payer-Specific Overrides

Different payers may have different timely filing requirements:

```json
{
  "payerSpecificOverrides": {
    "MEDICARE": {
      "payerId": "MEDICARE",
      "payerName": "Medicare",
      "ecsValueAdds277Override": {
        "integrationFlags": {
          "appeals": true
        },
        "appealConfiguration": {
          "timelyFilingDays": 120,
          "defaultAppealType": "Redetermination"
        }
      }
    },
    "UNITED": {
      "payerId": "UNITED",
      "payerName": "UnitedHealthcare",
      "ecsValueAdds277Override": {
        "integrationFlags": {
          "appeals": true
        },
        "appealConfiguration": {
          "timelyFilingDays": 365,
          "defaultAppealType": "Reconsideration"
        }
      }
    }
  }
}
```

## Business Logic Rules

### Timely Filing Validation

Appeals module validates timely filing before accepting appeal:

```javascript
function isTimelyFiling(claim, today = new Date()) {
  const deadline = parseDate(claim.appealTimelyFilingDate); // CCYYMMDD -> Date
  return today <= deadline;
}

function getAppealStatus(claim) {
  if (!claim.eligibleForAppeal) {
    return { canAppeal: false, reason: 'Claim status does not allow appeals' };
  }
  
  if (!isTimelyFiling(claim)) {
    return { canAppeal: false, reason: 'Timely filing deadline has passed' };
  }
  
  return { canAppeal: true, reason: null };
}
```

### Appeal Eligibility Checks

Before initiating appeal, perform these checks:

1. ✅ `eligibleForAppeal === true`
2. ✅ Current date ≤ `appealTimelyFilingDate`
3. ✅ Provider is authorized for this claim
4. ✅ Claim has not already been appealed (check tracking system)
5. ✅ Required appeal documentation is available

## Testing

### Test Scenario 1: Denied Claim Appeal Flow

**Given:**
- Claim status is "Denied"
- Timely filing deadline is 60 days away

**When:**
- Provider queries claim via ECS
- ECS returns `eligibleForAppeal: true`

**Then:**
- UI displays "Dispute Claim" button
- Clicking button navigates to appeals flow
- Appeal form pre-populated with claim data

### Test Scenario 2: Partially Paid Claim Appeal

**Given:**
- Claim status is "Partially Paid"
- Provider disputes underpaid amount

**When:**
- Provider queries claim via ECS
- ECS returns `eligibleForAppeal: true`

**Then:**
- UI shows appeal option for underpayment
- Appeal type is "Reconsideration"
- Provider can submit dispute with explanation

### Test Scenario 3: Paid Claim (No Appeal)

**Given:**
- Claim status is "Paid"
- Claim paid in full

**When:**
- Provider queries claim via ECS
- ECS returns `eligibleForAppeal: false`

**Then:**
- UI does NOT show "Dispute Claim" button
- Appeal section indicates appeal not available
- Provider sees complete payment details

### Test Scenario 4: Expired Timely Filing

**Given:**
- Claim status is "Denied"
- Timely filing deadline is in the past

**When:**
- Provider attempts to initiate appeal

**Then:**
- Appeals module rejects appeal
- Error message: "Timely filing deadline has passed"
- UI suggests contacting payer for late appeal consideration

## Error Handling

### Common Error Scenarios

1. **Missing appealTimelyFilingDate**
   - Fallback: Calculate deadline using default (180 days)
   - Log warning in Application Insights

2. **Invalid appealType**
   - Fallback: Use "Reconsideration"
   - Log warning

3. **ECS eligibleForAppeal but Appeals module disabled**
   - Show user-friendly message
   - Suggest contacting support

4. **Network error during appeal submission**
   - Queue appeal for retry
   - Show "Appeal Pending" status to provider

## Monitoring & Metrics

Track these metrics in Application Insights:

- **Appeal Initiation Rate**: % of denied claims that result in appeals
- **Appeals from ECS**: Count of appeals initiated via ECS integration
- **Timely Filing Compliance**: % of appeals filed before deadline
- **Appeal Success Rate**: % of appeals that result in payment
- **Average Time to Appeal**: Time from denial to appeal initiation

**Sample Kusto Query:**
```kusto
customEvents
| where name == "AppealInitiated"
| where customDimensions.source == "ECS"
| summarize count() by bin(timestamp, 1d)
| render timechart
```

## References

- [ECS-INTEGRATION.md](./ECS-INTEGRATION.md) - ECS ValueAdds277 documentation
- [ARCHITECTURE.md](../ARCHITECTURE.md) - System architecture
- Appeals Logic App workflow: `logicapps/workflows/process_appeals/workflow.json`
- ECS Logic App workflow: `logicapps/workflows/ecs_summary_search/workflow.json`
- Configuration schema: `config/schemas/availity-integration-config.schema.json`
