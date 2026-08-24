# Stedi 277CA hardening evidence

Date: 2026-08-24 UTC

Branch: `feat/stedi-277ca-hardening`

## Live Stedi 277CA

**Not executed.** Same constraint as #1112: sandbox accounts cannot submit
test claims.

```
Contract-tested against Stedi's documented 277CA format;
live acknowledgment pending production-account test access.
```

## Hardening proofs (synthetic)

| Scenario | Result |
| --- | --- |
| 12 concurrent processors, same acknowledgment id | 1 record, 1 `AcknowledgmentAccepted` transition, 1 Accepted event |
| Persist succeeds, bus fails, `DispatchPendingAsync` | outbox `PublishedAtUtc` set; Received+Accepted published once |
| Missing `claimStatus` | `Malformed`, not Accepted |
| Production + `Store=InMemory` | startup `InvalidOperationException` |
| 837 then 277CA accepted/rejected/partial, same submit key | no second HTTP; status preserved |
| New claim version after 277CA | second HTTP allowed |
| Unmatched 277CA | `UnableToMatch`, empty tenant, not attached |

No API keys, raw 277CA payloads, member names, or member IDs are recorded here.
