# Claims Examiner Service

The claims-examiner-service is an AI-powered advisory service that produces recommendations for pended claims. It is not an adjudication engine — it provides structured recommendations that a human claims examiner reviews before taking action.

## Table of contents

- [Architecture](#architecture)
- [Scope (v1)](#scope-v1)
- [Processing pipeline](#processing-pipeline)
- [Prompt design](#prompt-design)
- [Disposition model](#disposition-model)
- [Human-in-the-loop](#human-in-the-loop)
- [Safety model](#safety-model)

---

## Architecture

```
claims-service                 claims-examiner-service
     │                                │
     │ PUT /api/claims/{id}/pend      │
     │ publishes claims.pended.v1     │
     │ ─────────────────────────────► │
     │              Kafka             │
     │                                │ ClaimPendedConsumer
     │                                │   ▼
     │                                │ ExaminerOrchestrator
     │                                │   │ scope filter (NCCI only)
     │                                │   │ select addressable edit
     │                                │   │ fetch full claim
     │                                │   │ fetch RFAI history (optional)
     │                                │   ▼
     │                                │ ExaminerPromptBuilder
     │                                │   │ system prompt
     │                                │   │ user message
     │                                │   │ tool schema
     │                                │   ▼
     │                                │ AnthropicClient
     │                                │   │ Claude API (forced tool use)
     │                                │   ▼
     │                                │ recommend_disposition tool call
     │                                │   │
     │ PUT /api/claims/{id}/          │   │
     │       ai-examination  ◄────────│───┘
     │                                │
     ▼                                │
Work Queue (Blazor Portal)            │
  Human examiner reviews              │
  AI recommendation alongside claim   │
```

**No HTTP controllers** — the service exposes only health endpoints. All processing is driven by Kafka consumption.

---

## Scope (v1)

The v1 scope is deliberately narrow:

| Dimension | v1 boundary |
|-----------|-------------|
| Pend codes | `NCCI` only. AUTH, COB, MEDREVIEW, OON skipped. |
| Edit types | NE001 (PTP pair) with ModifierIndicator=1 only. MUE (NE002) skipped. |
| Edits per claim | First addressable edit only. Multi-edit is phase 2. |
| RFAI enrichment | Optional. NoOp default, wired when rfai-service has aggregate endpoint. |
| Auto-apply | Never. All recommendations require human review. |

---

## Processing pipeline

`ExaminerOrchestrator.ProcessAsync(ClaimPendedEvent)`:

1. **Scope filter** — Skip if pend code is not `NCCI`
2. **Select addressable edit** — Find first NE001 edit with `IsModifierAddressable() == true`
3. **Fetch full claim** — `GET /api/claims/{id}` on claims-service. 404 is benign (claim voided).
4. **Fetch RFAI history** (optional) — Provider's historical RFAI behavior for this edit type
5. **Build prompt** — Deterministic system prompt + structured user message
6. **Call Claude** — Forced tool use with `recommend_disposition` schema
7. **Project result** — Map tool call arguments to `AiExaminationDto`
8. **Write examination** — `PUT /api/claims/{id}/ai-examination` on claims-service

**Error handling:** All errors fall back to `EscalateToHuman` with confidence 0. The Kafka consumer always commits the offset — poison messages are logged and skipped, never retried in a loop.

---

## Prompt design

### System prompt

Establishes the examiner's narrow role:

- The NCCI engine has already identified the bundling edit (no second-guessing)
- The only question: should the edit be overridden by a -59/X{EPSU} modifier?
- Disposition rules with explicit conservative bias
- Confidence calibration guidelines (0.90+ = professional reputation)
- Rationale rules: 3–6 sentences, cite specific claim evidence, never invent facts

### User message

Structured, deterministic format containing:

- **Edit failure details** — Column 1/2 codes, rule ID, affected lines, modifier presence
- **Claim header** — ID, member, provider, place of service, dates, billed amount
- **Diagnosis codes** — All ICD-10 codes with pointer numbers
- **Service lines** — Full line detail with procedure codes, modifiers, diagnosis pointers, POS, dates
- **Provider RFAI history** (optional) — Total RFAIs, response rate, average response time
- **Task** — Explicit instruction to use the tool

### Tool schema

```json
{
  "name": "recommend_disposition",
  "input_schema": {
    "properties": {
      "recommended_disposition": {
        "type": "string",
        "enum": ["Approve", "Deny", "RequestInfo", "EscalateToHuman"]
      },
      "confidence_score": { "type": "number", "minimum": 0, "maximum": 1 },
      "rationale": { "type": "string" },
      "policy_citations": { "type": "array", "items": { "type": "string" } }
    },
    "required": ["recommended_disposition", "confidence_score", "rationale", "policy_citations"]
  }
}
```

---

## Disposition model

| Disposition | Meaning | When to use |
|-------------|---------|-------------|
| `Approve` | Override edit, pay as billed | Valid modifier present AND diagnosis/dates support distinct service |
| `RequestInfo` | Ask provider to substantiate | Distinct service plausible but documentation insufficient |
| `Deny` | Bundling stands | Documentation contradicts distinct service claim |
| `EscalateToHuman` | Cannot reach confident conclusion | Safe default. Used freely. |

---

## Human-in-the-loop

The claims-service work queue displays the AI recommendation alongside the claim:

- **Accept** — Apply the recommended disposition as-is
- **Modify** — Change the disposition (e.g., downgrade Approve to RequestInfo)
- **Override** — Reject the AI recommendation entirely

Agreement/disagreement is tracked for model calibration. Stale recommendations are rejected — the claim must still be in `Pended` status.

---

## Safety model

| Safeguard | Implementation |
|-----------|---------------|
| Never auto-applies | All recommendations require human action |
| Forced tool use | Model must call `recommend_disposition`, no free-text turns |
| Confidence gating | Below 0.50 → should use EscalateToHuman |
| Error fallback | Any Claude API failure → EscalateToHuman with confidence 0 |
| Scope lockdown | Only NCCI pends, only modifier-addressable edits |
| Audit trail | PromptVersion, ModelId, GeneratedAt stored on every examination |
| Staleness check | Claims-service rejects examination if claim status changed |
