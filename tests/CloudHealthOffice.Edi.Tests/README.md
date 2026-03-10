# CloudHealthOffice.Edi.Tests

Purpose: regression protection for X12 serialization/parsing paths where subtle format issues can cause trading-partner rejection.

## Current Coverage

### 835 ERA (`EraGeneratorService`)
- Core envelope and transactional segments (`ISA/GS/ST/BPR/TRN/N1/CLP/SVC/CAS/PLB/SE/GE/IEA`)
- `SE01` segment count integrity (ST..SE inclusive)
- CAS grouping and representative line/claim adjustments

### 277CA (`ClaimAcknowledgmentService`)
- Claim status → `STC01`/action mapping validation across multiple statuses
- Core hierarchy and trace segments (`HL/NM1/TRN/STC/DTP`)
- Optional payer claim control reference (`REF*1K`) behavior

### 270 Parser (`Edi270Parser`)
- ISA/GS/ST extraction and interchange/application IDs
- Subscriber/provider/payer extraction from NM1/REF/DMG/DTP/EQ
- Empty-input validation path

### 271 Generator (`Edi271Generator`)
- Covered flow (`EB`/`REF`/`TRN`/`DTP`/benefit messaging)
- Not-covered flow (`EB*6`, `AAA`, `MSG`)
- `SE01` segment count integrity (ST..SE inclusive)

## Bugs Found by Tests
- 835 `SE01` off-by-one fixed in `EraGeneratorService`
- 271 `SE01` off-by-one fixed in `Edi271Generator`

## Remaining High-Value Tests
1. 271 dependent loop generation (`HL 2000D`, `NM1*QC`, dependent `DMG`)
2. 270 parser delimiter variants and malformed ISA handling
3. 835 edge cases:
   - `CHK` vs `NON` payment branches
   - multiple CAS chunks (>6 adjustments)
   - multiple PLB chunks (>6 adjustments)
4. 277CA escaping/sanitization behavior for names containing X12 delimiters
5. Snapshot-style “known-good” fixtures per trading-partner profile

## Guidelines
- Prefer deterministic assertions over full-string equality where timestamps/control numbers are dynamic.
- Always assert `SE01` count correctness for generated X12 transaction sets.
- Keep synthetic data explicit; never include real PHI/PII in tests.
