# Claims Page Redesign - Implementation Guide

## Overview

This comprehensive redesign transforms the Claims management page from a simple list view to an advanced claims examiner workbench with search-first pattern, professional/institutional claim differentiation, and complete claim lifecycle management.

## Architecture & Design Patterns

### Search-First Pattern
The redesigned claims page follows the Benefit Plans pattern:
- **Initial State**: Empty search interface with multiple filter criteria
- **User Action**: Enter search criteria and click "Search"
- **Results Display**: Summary statistics cards + paginated results table
- **Progressive Disclosure**: Advanced options hidden by default, expandable

### Dual-Claim-Type Support
The system now handles two distinct claim structures:
- **Professional Claims (837P)**: CPT/HCPCS codes, office-based procedures, individual modifiers
- **Institutional Claims (837I)**: Revenue codes, facility charges, room/board, ancillary services

Different views conditionally render type-specific fields (e.g., revenue codes only for institutional)

### Claims Examiner Workflow
The claims detail page supports the complete adjudication lifecycle:
- **Examination Queue**: Search/filter pended or in-adjudication claims
- **Review & Adjudicate**: Approve, deny, or request additional information
- **Reversals & Adjustments**: Initiate reversals for paid claims, track adjustment history
- **Notes & Audit Trail**: Comprehensive documentation of all changes

## New/Modified Files

### DTOs & Services

#### `/src/portal/CloudHealthOffice.Portal/Services/IServices.cs`
**Changes:**
- **New Methods**: `SearchClaimsAsync()`, `UpdateClaimStatusAsync()`
- **Expanded ClaimSummary** with 28 new fields:
  - `ClaimNumber`, `ClaimType`, `AllowedAmount`, `PaidAmount`
  - Service date range, adjudication date, prior auth number
  - Line count, and more
- **Expanded ClaimDetails** with 40+ new fields:
  - Subscriber/patient info, billing/rendering/facility provider NPIs
  - Cost breakdown (deductible, coinsurance, copay)
  - Diagnosis codes (ICD-10) with pointers
  - Service lines with full 837 details (modifiers, revenue codes, diagnosis pointers)
  - Adjustment history, editable flags, audit trail
- **New DTOs**:
  - `ClaimSearchRequest`: Multi-criteria search (claim number, member, provider, claim type, status, date range, authorization number)
  - `ClaimSearchResult`: Paginated results with summary statistics
  - `ClaimDiagnosisCode`: ICD-10 code with description and pointer
  - `ClaimServiceLine`: Full service line with procedures, modifiers, adjustments
  - `ClaimLineAdjustment`: CARC codes and adjustment reasons
  - `ClaimAdjustmentInfo`: Reversal/adjustment tracking
  - `ClaimAudit`: Change history entries

#### `/src/portal/CloudHealthOffice.Portal/Services/ServiceImplementations.cs`
**Changes:**
- **New Method**: `SearchClaimsAsync()` with server fallback to mock data
- **New Method**: `UpdateClaimStatusAsync()` for status transitions
- **Enhanced Mock Data**: 
  - `GetMockClaims()` now returns 100 claims with full details
  - `GetMockSearchResults()` applies filters and pagination
  - `GetMockClaimDetails()` returns comprehensive claim with all 837 fields
- **Mock Storage**: Claims include diagnosis codes, service line details, adjustment reasons, audit trail

### UI Components

#### `/src/portal/CloudHealthOffice.Portal/Pages/ClaimsNew.razor` (NEW)
**Features:**
- **Search Form**:
  - Claim number (text)
  - Member lookup (autocomplete with member ID/DOB display)
  - Provider lookup (autocomplete with NPI/specialty)
  - Authorization number (text)
  - Claim type dropdown (Professional/Institutional)
  - Status dropdown (Submitted, Received, InAdjudication, Pended, Approved, Denied, Paid, PartiallyPaid, Voided)
  - Service date range picker
  - Advanced options toggle (sort by, sort order, page size)
- **Validation**: At least one search criterion required
- **Results Display**:
  - Summary cards: Total results, total charges, approved count, pending count, denied count
  - Searchable, sortable, paginated table
  - Claim type badges (P for Professional, I for Institutional)
  - Status color-coded chips
  - Days since submission calculation
  - Click-through to details
- **Pagination**: Custom pagination for multi-page results
- **Empty State**: User-friendly message when no results

#### `/src/portal/CloudHealthOffice.Portal/Pages/ClaimDetailsNew.razor` (NEW)
**Features:**
- **Header Section**:
  - Claim number with prior auth chip
  - Status and claim type badges
  - Action buttons: Approve, Deny, Initiate Reversal (state-dependent)
  - Finalized claim warning if not editable
- **Financial Summary**:
  - 4 key cards: Total charges, allowed amount, payer payment, patient responsibility
- **Member & Provider Information Panels**:
  - Subscriber, patient (if different), member ID
  - Billing provider (NPI required)
  - Rendering provider (if different, optional)
  - Facility (if applicable, optional)
- **Dates & Cost Breakdown Tables**:
  - Service dates from/to, submitted, received, adjudicated, paid
  - Charges, allowed, deductible, coinsurance, copay, patient resp, payer amount
- **Diagnosis Codes Section**:
  - Expandable table: Code, description, type (principal/secondary), pointer number
  - ICD-10 format
- **Service Lines - Expandable Detail Section**:
  - Summary row: Line #, CPT/HCPCS code, description, units, charge amount, status
  - Expanded per line:
    - **Amounts**: Charge, allowed, paid, patient responsibility
    - **Modifiers**: Up to 4, displayed as chips
    - **Related Diagnoses**: Pointer references back to diagnosis codes
    - **Adjustments**: CARC codes (group + reason), amounts, descriptions (only if adjusted)
    - **Revenue Code** (institutional claims only)
- **Claim Adjustment History Section**:
  - Type (reversal, adjustment, correction)
  - Original claim link (if applicable)
  - Adjustment amount (highlighted)
  - Date and performed by user
- **Notes & Comments Section**:
  - Display existing claim notes
  - Text input to add internal notes (if editable)
  - Add Note button
- **Change History (Audit Trail)**:
  - Timeline view (MUD Timeline component)
  - Each entry: Action, timestamp, changed by user
  - Old/new values for field changes
  - Associated notes/explanations
- **Action Bar**:
  - Back to claims button
  - Print button (UI only)
  - Export EOB button (UI only)

#### `/src/portal/CloudHealthOffice.Portal/Dialogs/DenyClaimDialog.razor` (NEW)
**Features:**
- **Denial Reason Selection**:
  - Dropdown with 8 common reasons:
    - Medical necessity not established
    - Service not covered under plan
    - Exceeds plan limits
    - Prior authorization required
    - Duplicate claim
    - Exceeds frequency limit
    - Age/gender restrictions
    - Other (custom text)
  - Custom reason field appears for "Other" selection
- **Additional Notes Field**: Optional context
- **Dialog Actions**: Cancel, Deny Claim button (disabled until reason selected)

#### `/src/portal/CloudHealthOffice.Portal/Dialogs/ReversalDialog.razor` (NEW)
**Features:**
- **Reversal Type Selection**:
  - Full Reversal (entire claim)
  - Partial Reversal (selected lines, with line selection checkboxes)
  - Duplicate Adjustment
- **Reversal Reason Field**: Required text area
- **Reversal Method Selection**:
  - Zero Payment Reversal (set payment to $0)
  - Negative Adjustment (issue provider credit)
- **Provider Notification Checkbox**: Notify provider of reversal
- **Dialog Actions**: Cancel, Initiate Reversal button

## Data Flow

### Search Flow
```
User Input (ClaimsNew.razor)
  ↓ Validation (at least 1 criterion)
  ↓ ClaimSearchRequest object
  ↓ ClaimsService.SearchClaimsAsync()
  ↓ HTTP POST to /api/claims/search (backend falls back to mock)
  ↓ ClaimSearchResult (paginated, with summary stats)
  ↓ Display results table + summary cards
```

### Detail View Flow
```
User clicks claim row or claim number link
  ↓ Navigate to /claims/{ClaimId}
  ↓ ClaimDetailsNew.razor OnInitializedAsync()
  ↓ ClaimsService.GetClaimByIdAsync(ClaimId)
  ↓ HTTP GET /api/claims/{ClaimId}
  ↓ ClaimDetails (comprehensive 837 data)
  ↓ Render with conditional sections (type, status, editable state)
```

### Status Update Flow
```
User clicks Approve/Deny/Reverse button
  ↓ Dialog opens (DenyClaimDialog or ReversalDialog)
  ↓ User confirms with reason/details
  ↓ ClaimsService.UpdateClaimStatusAsync(claimId, status, notes)
  ↓ HTTP PUT /api/claims/{claimId}/status
  ↓ Reload claim details via GetClaimByIdAsync()
  ↓ UI updates with new status and audit trail entry
```

## Search Criteria & Filtering

### Available Filters
| Field | Type | Description | Notes |
|-------|------|-------------|-------|
| Claim Number | Text | Exact match | Unique identifier |
| Member | Autocomplete | Search by ID/name/DOB | Filtered via MemberService |
| Provider | Autocomplete | Search by NPI/name | Filtered via ProviderService |
| Authorization # | Text | Prior authorization number | Optional |
| Claim Type | Dropdown | Professional (837P) / Institutional (837I) | Exclusive |
| Status | Dropdown | See statuses list | Single select |
| Service Date | Date Range | From/To picker | Both inclusive |

### Search Validation
- At least one criterion required
- If none provided, warning message appears

### Sorting & Pagination
- **Sort Options**: SubmittedDate (default), ServiceDate, Amount, Status
- **Sort Order**: Descending (default) or Ascending
- **Page Size**: 10-100 items per page (default 25)
- **Pagination**: Click page numbers to navigate

## Claim Status Lifecycle

```
Submitted (initial)
  ↓
Received (acknowledged by payer)
  ↓
InAdjudication (under review)
  ├→ Pended (needs more info or examiner review)
  │    ├→ Approved (after examiner reviews)
  │    └→ Denied (after examiner reviews)
  │
  ├→ Approved (automatic or manual)
  │    ├→ Paid (payment issued)
  │    └→ PartiallyPaid (if line-level denials)
  │
  └→ Denied (automatic or manual)

Additional States:
- Voided: Entire claim reversed
- PartiallyPaid: Some lines approved, some denied
```

## Claim Editability Rules

| Status | Editable | Can Approve | Can Deny | Can Reverse |
|--------|----------|-------------|----------|------------|
| Submitted | No | No | No | No |
| Received | No | No | No | No |
| InAdjudication | Yes | Yes | Yes | No |
| Pended | Yes | Yes | Yes | No |
| Approved | No | No | No | Yes |
| Denied | No | No | No | No |
| Paid | No | No | No | Yes |
| PartiallyPaid | No | No | No | Yes |
| Voided | No | No | No | No |

## 837 Field Mappings

### Supported Field Groups

#### Subscriber/Patient Info (N3, NM1 2010BA, 2010CA)
- Subscriber ID, name, relationship to subscriber
- Patient name, relationship, date of birth (if dependent)

#### Billing & Facility Info (NM1 2010AA, 2310B, 2310C)
- Billing provider NPI + name
- Rendering provider NPI + name (optional)
- Facility NPI + name (optional)

#### Clinical Data (HI segment)
- Diagnosis codes (ICD-10): principal + up to 11 secondary
- Service lines (2400 loop):
  - CPT/HCPCS codes with up to 4 modifiers
  - Units, charge amount
  - Service date range
  - Revenue code (institutional)
  - Diagnosis pointers (links to diagnosis codes)

#### Adjudication Data (835 Remittance)
- Allowed amount, patient responsibility breakdown (deductible, coinsurance, copay)
- Line-level adjustments (CARC codes)
- Remark codes, check number
- Payment date

#### Audit Trail
- Status changes with timestamp and user
- Field-level changes with old/new values
- Notes attached to changes

## Mock Data Strategy

### Claim Generation
- **Count**: 100 mock claims in memory
- **Claim Types**: Mix of Professional (67%) and Institutional (33%)
- **Service Dates**: Last 90 days
- **Status Distribution**: Approved (40%), Pended (20%), Denied (10%), InAdjudication (20%), others (10%)
- **Providers/Members**: 5 providers, 8 members (realistic cross-tabulation)

### Per-Claim Details
- **Service Lines**: 1-5 lines per claim with CPT/HCPCS codes
- **Diagnosis Codes**: 1-3 diagnoses (ICD-10) with pointer references
- **Adjustments**: Only for denied/pended claims (realistic)
- **Audit Trail**: 2-4 entries showing workflow progression

### Mock Limitations
- No real database persistence
- Fallback when backend unavailable
- Sufficient for UI/UX validation

## Integration Points

### Backend APIs (Expected)
```
POST /api/claims/search
  Request: ClaimSearchRequest
  Response: ClaimSearchResult
  Status: 200, 400, 500

GET /api/claims/{claimId}
  Response: ClaimDetails
  Status: 200, 404, 500

PUT /api/claims/{claimId}/status
  Request: { status: string, notes?: string }
  Response: 200, 400, 404, 500

GET /api/members/search?q={searchTerm}
  Response: List<MemberSummary>
  Status: 200, 500

GET /api/providers/search?q={searchTerm}
  Response: List<ProviderSummary>
  Status: 200, 500
```

## Feature Highlights

1. **Comprehensive Search**
   - Multi-criteria filtering
   - Autocomplete for names/IDs
   - Date range support
   - Real-time result summary

2. **Professional/Institutional Views**
   - Conditional field display
   - Type-specific summary cards
   - Revenue code support

3. **Claims Examiner Workflow**
   - Approve/deny dialogs with reason tracking
   - Reversal initiation with options
   - Notes section for internal documentation
   - Audit trail for compliance

4. **Financial Detail**
   - Line-level cost breakdown
   - Adjustment reason codes (CARC)
   - Patient cost-share calculation
   - Payer vs patient split visible

5. **Editable State Management**
   - Context-aware UI (buttons, warnings)
   - Finalized claim protection
   - Reversal restrictions

6. **User Experience**
   - Empty state guidance
   - Loading indicators
   - Error handling with snackbar
   - Breadcrumb navigation
   - Timeline-based audit history

## Testing Scenarios

1. **Search Validation**: No criteria → warning message
2. **Member Autocomplete**: Type "Johnson" → Sarah Johnson appears in dropdown
3. **Provider Search**: Type "1234" → matches on NPI
4. **Status Filter**: Select "Pended" → only pended claims shown
5. **Approve Flow**: Click Approve → approve button disabled until reason entered → claim status updated
6. **Denial Tracking**: Select denial reason → dialog shows selected option → notes recorded in audit trail
7. **Reversal Init**: Click Initiate Reversal → dialog shows reversal type options → creates adjustment info entry
8. **Finalized Claim**: View paid claim → all edit buttons disabled, warning shown
9. **Line Details**: Click expand on service line → shows modifiers, adjustments, related diagnoses
10. **Diagnosis Pointers**: Claims with diagnosis/procedure links → highlighted in respective tables

## Future Enhancements

1. **Batch Operations**
   - Multi-select claims for bulk approve/deny
   - Batch reversal requests

2. **Advanced Reporting**
   - Denial reason trends
   - Adjudication time analytics
   - Claims examiner performance metrics

3. **Integrations**
   - Real-time EDI 835 receipt
   - Automatic remittance posting
   - Payment posting workflow

4. **Additional Workflow**
   - Appeal submission directly from claim
   - Attachment upload (medical records, justification)
   - Provider inquiry submission

5. **Mobile Support**
   - Responsive design for tablets
   - Touch-optimized dialogs
  
## Deployment Notes

1. Update service registration in `Program.cs` (already configured for existing claims service)
2. Update navigation menu to link `/claims-new` instead of `/claims`
3. Monitor mock data API fallback responses
4. Configure backend endpoints per environment
5. Set up proper audit logging in production

---

**Version**: 1.0  
**Last Updated**: March 2026  
**Status**: Ready for Integration Testing
