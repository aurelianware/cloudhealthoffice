# Validation Tools Quick Reference

This document provides quick reference for the validation tools added in PR #4.

## EDI X12 Validator

**Script**: `scripts/validate-edi-x12.ps1`

### Purpose
Validates HIPAA X12 EDI files (275/277/278) for format compliance.

### Usage

```powershell
# Validate a single EDI file
pwsh -File scripts/validate-edi-x12.ps1 -Path test-x12-275-availity-to-pchp.edi

# Validate with specific transaction type
pwsh -File scripts/validate-edi-x12.ps1 -Path test-file.edi -TransactionType 275

# Validate all EDI files in current directory with strict checking
pwsh -File scripts/validate-edi-x12.ps1 -Path . -Strict
```

### What It Validates

- ✅ ISA/IEA envelope segments
- ✅ GS/GE functional group segments
- ✅ ST/SE transaction set segments
- ✅ Trading partner identifiers (Availity: 030240928, Health Plan: {config.payerId})
- ✅ Transaction types (275/277/278)
- ✅ Segment counts
- ✅ X12 structure and format

### Example Output

```
🏥 HIPAA X12 EDI Validator
==========================

📄 Validating: test-x12-275-availity-to-pchp.edi
ℹ️  Found 20 segments
ℹ️  Trading partners validated: 030240928 -> {config.payerId}
ℹ️  Detected transaction type: 275 (Attachment Request)

==================================================
📊 Validation Summary
==================================================
✅ All validations PASSED
```

## Security Scanner

**Script**: `scripts/scan-for-phi-pii.ps1`

### Purpose
Scans code for PII/PHI data and HIPAA compliance issues.

### Usage

```powershell
# Scan entire repository
pwsh -File scripts/scan-for-phi-pii.ps1 -Path . -Exclude ".git","node_modules"

# Scan specific directory
pwsh -File scripts/scan-for-phi-pii.ps1 -Path logicapps/

# Fail on warnings (stricter mode)
pwsh -File scripts/scan-for-phi-pii.ps1 -Path . -FailOnWarning
```

### What It Detects

**PII/PHI Patterns:**
- Social Security Numbers (SSN) - formatted (XXX-XX-XXXX)
- Medical Record Numbers (MRN)
- Date of Birth (DOB)
- Credit Card Numbers
- Email Addresses
- Phone Numbers
- Patient/Member IDs

**Secrets:**
- Azure API Keys/Secrets
- Hardcoded Passwords
- Connection Strings
- Private Keys
- JWT Tokens

**HIPAA Compliance:**
- Unencrypted PHI transmission (HTTP instead of HTTPS)
- PHI in console logs

### Context-Aware Filtering

The scanner intelligently excludes:
- Test files (`test-*.edi`, `test-*.json`)
- Documentation files (`*.md`)
- Trading partner IDs (030240928, {config.payerId})
- Pattern definitions in scanner itself

### Example Output

```
🔐 HIPAA PII/PHI Security Scanner
=================================
🔍 Scanning 43 files...

============================================================
📊 Security Scan Summary
============================================================
Files scanned: 43
Issues found: 0

✅ No security issues detected
```

## Smoke Tests

**Workflow**: `.github/workflows/sanity.yml`

### Purpose
Comprehensive smoke tests to validate repository health.

### Triggers
- Manual: `workflow_dispatch`
- Automatic: Push to `main` or `release/*` branches
- Pull requests to `main` or `release/*` branches

### What It Tests

1. **Repository Structure**: Required directories and files
2. **Logic Apps Workflows**: All 4 workflow JSON files
3. **Bicep Templates**: Compilation and syntax
4. **Service Bus Configuration**: Topic definitions
5. **X12 Schemas**: Schema file presence
6. **PowerShell Scripts**: Syntax validation
7. **EDI Files**: Format validation
8. **Security Scanning**: PII/PHI detection
9. **Test Data**: Test file validation

### Running Manually

```bash
# Via GitHub UI
Go to Actions → Smoke Tests → Run workflow

# Via gh CLI
gh workflow run sanity.yml
```

## PR Lint Checks

**Workflow**: `.github/workflows/pr-lint.yml`

### Purpose
Comprehensive linting and validation for pull requests.

### What It Checks

1. **JSON Validation**: Logic App workflow files
2. **GitHub Actions Lint**: actionlint
3. **YAML Lint**: yamllint
4. **Bicep Validation**: Template compilation
5. **PowerShell Analysis**: PSScriptAnalyzer (errors only)
6. **PowerShell Syntax**: Basic syntax validation
7. **EDI Validation**: X12 format (strict mode)
8. **Secret Detection**: Trufflehog
9. **PII/PHI Scanning**: Custom scanner
10. **File Encoding**: UTF-8 validation
11. **HIPAA Compliance**: Pattern checks

### Automatic Execution

Runs automatically on all pull requests to:
- `main`
- `release/*`
- `feature/*`

## Local Validation

### Quick Check Script

Create this script to run all validations locally:

```bash
#!/bin/bash
# validate-all.sh

echo "Running comprehensive validation..."

# JSON validation
echo "1. Validating JSON workflows..."
find logicapps/workflows -name "workflow.json" -exec jq . {} \; > /dev/null

# Bicep validation
echo "2. Validating Bicep templates..."
az bicep build --file infra/main.bicep --outfile /tmp/arm.json > /dev/null

# PowerShell validation
echo "3. Validating PowerShell scripts..."
shopt -s nullglob
for script in *.ps1; do
  pwsh -Command "Get-Content './$script' | Out-Null"
done
shopt -u nullglob

# EDI validation
echo "4. Validating EDI files..."
pwsh -File scripts/validate-edi-x12.ps1 -Path . -Strict

# Security scan
echo "5. Running security scan..."
pwsh -File scripts/scan-for-phi-pii.ps1 -Path . -Exclude ".git"

echo "✅ All validations complete!"
```

### Individual Checks

```bash
# JSON only
find logicapps/workflows -name "workflow.json" -exec jq . {} \;

# Bicep only
az bicep build --file infra/main.bicep --outfile /tmp/arm.json

# PowerShell only
pwsh -Command "Get-Content './test-workflows.ps1' | Out-Null"

# EDI only
pwsh -File scripts/validate-edi-x12.ps1 -Path test-x12-275-availity-to-pchp.edi

# Security only
pwsh -File scripts/scan-for-phi-pii.ps1 -Path . -Exclude ".git"
```

## Troubleshooting

### EDI Validator Issues

**Problem**: "Invalid X12 structure" or "ISA segment format error"  
**Solution**: Ensure the EDI file starts with a valid ISA segment and contains proper envelope structure (ISA/GS/ST/SE/GE/IEA).

**Problem**: "Trading partners not validated"  
**Solution**: Check that ISA segment has correct sender/receiver IDs (030240928/{config.payerId})

**Problem**: "Segment count mismatch"  
**Solution**: Warning only. Verify SE segment count includes all segments from ST to SE.

### Security Scanner Issues

**Problem**: Too many false positives  
**Solution**: Add patterns to `AllowedFiles` or adjust `ExcludeContext`

**Problem**: Trading partner IDs flagged as SSN  
**Solution**: Already handled by context-aware filtering. Ensure file name or context contains "trading", "partner", "Availity", or "Health Plan"

### Workflow Issues

**Problem**: Trufflehog fails  
**Solution**: Now uses `continue-on-error: true`. Check logs but won't block PR.

**Problem**: PSScriptAnalyzer fails  
**Solution**: Only errors block builds. Fix reported errors or suppress specific rules if appropriate.

## Best Practices

1. **Run validations before committing**
   ```bash
   pwsh -File scripts/validate-edi-x12.ps1 -Path .
   pwsh -File scripts/scan-for-phi-pii.ps1 -Path . -Exclude ".git"
   ```

2. **Test EDI files after changes**
   ```powershell
   pwsh -File scripts/validate-edi-x12.ps1 -Path test-x12-275-availity-to-pchp.edi -Strict
   ```

3. **Scan before pushing sensitive code**
   ```powershell
   pwsh -File scripts/scan-for-phi-pii.ps1 -Path logicapps/ -FailOnWarning
   ```

4. **Use strict mode for critical files**
   ```powershell
   pwsh -File scripts/validate-edi-x12.ps1 -Path production.edi -Strict -TransactionType 275
   ```

## Additional Resources

- [HIPAA X12 Standards](https://www.wpc-edi.com/reference)
- [PSScriptAnalyzer Rules](https://github.com/PowerShell/PSScriptAnalyzer)
- [Trufflehog Documentation](https://github.com/trufflesecurity/trufflehog)
- [Azure Bicep Best Practices](https://learn.microsoft.com/en-us/azure/azure-resource-manager/bicep/best-practices)

## Support

For issues with validation tools:
1. Check this guide first
2. Review CONTRIBUTING.md for detailed documentation
3. Check GitHub Actions logs for specific errors
4. Open an issue with validation output and file samples
