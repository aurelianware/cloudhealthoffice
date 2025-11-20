# PHI/PII Scanner Guide

## Overview

The PHI/PII scanner (`scripts/scan-for-phi-pii.ps1`) is designed to detect potential Protected Health Information (PHI) and Personally Identifiable Information (PII) in code, preventing accidental exposure of sensitive data.

## Common False Positives and How to Avoid Them

### 1. Console.log / Logging Statements

**Problem**: The scanner flags ALL `console.log` statements, not just those with PHI.

**Solution**: The scanner now only flags logging statements that contain PHI-related terms (ssn, dob, patient, member id, mrn, etc.).

**Best Practices**:
- ✅ `console.log('Processing workflow...')` - Safe, no PHI terms
- ✅ `console.log('Generated deployment for ${payerName}')` - Safe
- ❌ `console.log('Patient SSN: ' + ssn)` - Flags correctly
- ❌ `console.log('Member ID: ' + memberId)` - Flags correctly

### 2. Test Data in Code

**Problem**: Test files with example phone numbers, emails, or IDs get flagged.

**Solution**: Test files are now allowed, and common test patterns are excluded.

**Best Practices**:
- Use clearly synthetic test data:
  - ✅ `test@test.com`, `john@test.com` - Recognized as test
  - ✅ `1234567890` (NPI/Provider ID) - Excluded when context shows it's test/example
  - ✅ `9999555001` - Clearly synthetic pattern
- Include "test", "example", "sample", "mock" in context:
  - ✅ `const testEmail = "user@example.com"`
  - ✅ `// Example: providerId: "1234567890"`

### 3. Configuration Files

**Problem**: Example configurations contain contact information.

**Solution**: Config files with patterns like `*-config.json`, `example-*.json` are allowed.

**Best Practices**:
- Name example configs appropriately:
  - ✅ `medicaid-mco-config.json`
  - ✅ `example-payer-config.json`
  - ✅ `test-integration-config.json`

### 4. Phone Numbers vs. NPIs/Tax IDs

**Problem**: 10-digit numbers like NPIs match phone number patterns.

**Solution**: Context-aware exclusion for provider IDs, NPIs, and tax IDs.

**Best Practices**:
- Use descriptive field names:
  - ✅ `"providerId": "1234567890"` - Excluded
  - ✅ `"providerNpi": "1234567890"` - Excluded
  - ✅ `"taxId": "12-3456789"` - Excluded
  - ❌ `"contact": "1234567890"` - May flag

## Scanner Configuration

### Allowed File Patterns

Files matching these patterns are allowed to contain certain data types:

#### Email Addresses
- `*.md` - Documentation
- `*.ps1` - PowerShell scripts
- `test-*.json`, `*-config.json`, `example-*.json` - Configuration
- `*.yml`, `*.yaml` - CI/CD configs
- `*.test.ts`, `*.test.js`, `*.spec.ts`, `*.spec.js` - Test files

#### Phone Numbers
- `*.edi` - EDI files (healthcare standard)
- `test-*.json`, `*-config.json`, `example-*.json` - Configuration
- `ecs-*.json`, `*valueadds*.json` - Specific example files
- `*.md`, `*.yml`, `*.yaml` - Documentation/CI
- `*.ts`, `*.js` - Source code (with context exclusions)

#### Logging PHI
- `*.md` - Documentation
- `*test*.ts`, `*test*.js` - Test files

### Exclusion Contexts

The scanner checks the context around matches and excludes:

- **Test/Example indicators**: `test`, `example`, `sample`, `mock`, `dummy`, `placeholder`
- **Provider identifiers**: `npi`, `provider`, `providerId`, `providerNpi`, `taxId`, `ein`
- **Synthetic patterns**: `1234567890`, `9999`, `TEST-`
- **Configuration markers**: `.com` domains like `test.com`, `example.com`

## Running the Scanner Locally

Before submitting a PR, run the scanner:

```bash
# Scan entire repository
pwsh -File scripts/scan-for-phi-pii.ps1 -Path .

# Scan specific directory
pwsh -File scripts/scan-for-phi-pii.ps1 -Path scripts/

# Scan specific file
pwsh -File scripts/scan-for-phi-pii.ps1 -Path core/examples/medicaid-mco-config.json
```

## Fixing Scanner Issues

### If you get false positives:

1. **Check if it's actually PHI**
   - Is it real patient/member data? → Fix it (remove or mask)
   - Is it test/example data? → Continue to step 2

2. **Use appropriate file naming**
   - Test files: `*.test.ts`, `test-*.json`
   - Examples: `example-*.json`, `*-example.ts`
   - Config: `*-config.json`

3. **Add context indicators**
   ```typescript
   // ✅ Good - Clear it's test data
   const testEmail = "john@test.com";
   const exampleProviderId = "1234567890";
   
   // ❌ May flag
   const email = "john@somecompany.com";
   const id = "1234567890";
   ```

4. **Use clearly synthetic patterns**
   ```typescript
   // ✅ Clearly synthetic
   const testNPI = "9999555001";
   const testPhone = "+1-555-0100";
   
   // ❌ Looks potentially real
   const npi = "1234567890";
   const phone = "214-555-1234";
   ```

### If you need to update the scanner:

1. Edit `scripts/scan-for-phi-pii.ps1`
2. Update the relevant pattern's `AllowedFiles` or `ExcludeContext`
3. Test locally before committing
4. Document changes in this guide

## CI/CD Integration

The scanner runs automatically in the `pr-lint.yml` workflow:

```yaml
- name: Scan for PII/PHI
  shell: pwsh
  run: |
    & ./scripts/scan-for-phi-pii.ps1 -Path . -Exclude ".git","node_modules",".terraform"
    if ($LASTEXITCODE -eq 1) {
      Write-Host "::error::Critical PII/PHI issues found"
      exit 1
    }
```

**Exit Codes**:
- `0` - No issues or only warnings
- `1` - Critical issues found (blocks PR)

## Pattern Reference

### Current Patterns

1. **SSN**: `\b\d{3}-\d{2}-\d{4}\b`
2. **MRN**: `\bMRN[:\s]*[A-Z0-9]{6,12}\b`
3. **DOB**: Date of birth patterns
4. **Email**: `\b[a-z0-9._%+-]+@[a-z0-9.-]+\.[a-z]{2,}\b`
5. **Phone**: `\b(?:\+?1[-.\s]?)?\(?\d{3}\)?[-.\s]?\d{3}[-.\s]?\d{4}\b`
6. **Member ID**: `\b(?:Member[_\s]?ID|MemberId)[:\s]*[A-Z0-9]{6,15}\b`
7. **Logging PHI**: `(?:console\.log|Write-Host).*(?:ssn|dob|patient|member.?id|mrn)`
8. **Secrets**: Azure keys, passwords, connection strings, JWT tokens
9. **Unencrypted PHI**: HTTP (not HTTPS) URLs with PHI terms

## Troubleshooting

### Scanner takes too long

The scanner may take 20-30 seconds on the full repository. This is normal for 100+ files.

### "Pattern not found" errors

The scanner uses PowerShell regex. Some patterns may behave differently than JavaScript regex.

### Need to add a new file type

Update the `AllowedFiles` array for the relevant pattern in `scan-for-phi-pii.ps1`.

## Best Practices Summary

1. ✅ Use descriptive field names for IDs (`providerId`, not just `id`)
2. ✅ Include "test", "example", or "mock" in test data context
3. ✅ Use clearly synthetic patterns (9999..., 555-0100, test.com)
4. ✅ Name test files appropriately (*.test.ts, test-*.json)
5. ✅ Name example configs appropriately (*-config.json, example-*.json)
6. ✅ Test locally before pushing
7. ❌ Never log real PHI (SSN, DOB, Member IDs, etc.)
8. ❌ Don't use realistic-looking test data
9. ❌ Don't hardcode real credentials or connection strings

## Getting Help

If the scanner is blocking your PR with false positives:

1. Review this guide
2. Check if your data is clearly marked as test/example
3. Verify file naming follows patterns
4. Update scanner patterns if needed (with documentation)
5. Ask for review if uncertain

## Version History

- **2025-11-20**: Enhanced patterns to reduce false positives
  - Fixed LoggingPHI pattern to only flag PHI-related terms
  - Added context exclusions for test data
  - Added TypeScript/JavaScript test file support
  - Improved phone number vs. NPI detection
