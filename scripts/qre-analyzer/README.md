# X12 278 X215 QRE Analyzer

Validates X12 278 Healthcare Services Review - Inquiry and Response (X215) transactions against Availity QRE (Query and Response for Eligibility) requirements and best practices.

## Overview

This analyzer ensures X12 278 inquiry transactions comply with:
- **X12 TR3 005010X215** specifications
- **Availity QRE** minimal data requirements
- **Best practices** for authorization inquiry transactions

## Features

- ✅ Validates X12 segment structure and ordering
- ✅ Checks ISA/GS/ST envelope format
- ✅ Enforces QRE minimal data principle
- ✅ Detects query method (by auth number or member demographics)
- ✅ Validates required and optional segments per TR3
- ✅ Configurable validation rules
- ✅ JSON output for CI/CD integration

## Prerequisites

- **Python 3.7+**
- No external dependencies (uses standard library only)

## Installation

### Quick Setup

```bash
# Run the setup script
chmod +x setup.sh
./setup.sh
```

### Manual Setup

```bash
# Make the analyzer executable
chmod +x x12_278_qre_analyzer.py

# Verify Python version
python3 --version  # Should be 3.7 or higher
```

## Usage

### Basic Usage

```bash
# Analyze an X12 278 EDI file
python3 x12_278_qre_analyzer.py path/to/file.edi
```

### With Custom Configuration

```bash
# Use custom config file
python3 x12_278_qre_analyzer.py path/to/file.edi custom-config.json
```

### Example Output

```
================================================================================
X12 278 X215 QRE Analysis Report
================================================================================
File: docs/examples/authorizations/inquiry/x215-inquiry-by-auth-number-request.edi
TR3 Version: 005010X215
Valid: ✓ YES
Errors: 0
Warnings: 0
Info: 1
Query Method: ByAuthorizationNumber
Segments Found: 12
--------------------------------------------------------------------------------

INFOS (1):
  QRE005 [REF]: Query method: Authorization Number (REF*D9 segment found)

================================================================================
```

## Configuration

The analyzer uses `qre-analyzer.config.json` for validation rules.

### Configuration Options

```json
{
  "version": "1.0.0",
  "tr3Version": "005010X215",
  "validationRules": {
    "strictMode": false,
    "validateSegmentOrder": true,
    "validateDataElements": true,
    "validateEnvelopes": true,
    "checkDuplicateSegments": true
  },
  "qreRequirements": {
    "minimalDataPrinciple": true,
    "requiredSegments": ["ISA", "GS", "ST", "BHT", "HL", "NM1", "TRN", "UM", "HCR", "SE", "GE", "IEA"],
    "optionalSegments": ["REF", "DMG", "DTP"],
    "queryMethods": {
      "byAuthorizationNumber": true,
      "byMemberDemographics": true
    }
  },
  "outputOptions": {
    "format": "json",
    "verbosity": "normal",
    "includePassedChecks": false,
    "outputPath": ""
  },
  "errorHandling": {
    "continueOnError": false,
    "maxErrors": 100,
    "failOnWarnings": false
  }
}
```

### Key Configuration Settings

- **strictMode**: Fail on any validation warning
- **minimalDataPrinciple**: Enforce Availity QRE minimal data best practice
- **queryMethods**: Enable validation for specific query methods
- **outputPath**: Write JSON report to file (empty = stdout only)
- **failOnWarnings**: Treat warnings as errors

## Query Methods

The analyzer detects and validates two query methods:

### 1. By Authorization Number

**Required segments:**
- `REF*D9*<authNumber>` - Authorization number reference

**Example:**
```
REF*D9*AUTH12345~
```

### 2. By Member Demographics

**Required segments:**
- `NM1*IL*1*<lastName>*<firstName>***MI*<memberId>` - Member identification
- `DMG*D8*<CCYYMMDD>` - Patient date of birth

**Example:**
```
NM1*IL*1*DOE*JOHN***MI*MEM123456~
DMG*D8*19850615~
```

## Validation Codes

### Errors (ENV/QRE/SYS)

| Code | Description |
|------|-------------|
| ENV001 | Missing ISA segment |
| ENV003 | Missing GS segment |
| ENV004 | Missing ST segment |
| ENV005 | Invalid transaction code (must be 278) |
| QRE001 | Missing required segment |
| SYS001 | System error (file read failure) |

### Warnings

| Code | Description |
|------|-------------|
| ENV002 | Multiple ISA segments found |
| ENV006 | Implementation guide version may not be 005010X215 |
| QRE002 | BHT01 should be '0007' for inquiry |
| QRE003 | UM segment recommended for QRE |
| QRE007 | Cannot determine query method |

### Info

| Code | Description |
|------|-------------|
| QRE004 | HCR action code information |
| QRE005 | Query method: Authorization Number detected |
| QRE006 | Query method: Member Demographics detected |

## CI/CD Integration

### Exit Codes

- **0**: Validation passed
- **1**: Validation failed (errors found)

### JSON Output

```bash
# Export to JSON file
python3 x12_278_qre_analyzer.py file.edi > report.json

# Or configure in config file
{
  "outputOptions": {
    "outputPath": "validation-report.json"
  }
}
```

### GitHub Actions Example

```yaml
- name: Validate X12 278 Inquiry
  run: |
    python3 scripts/qre-analyzer/x12_278_qre_analyzer.py \
      docs/examples/authorizations/inquiry/x215-inquiry-by-auth-number-request.edi
```

## Testing

Test the analyzer with example files:

```bash
# Test query by authorization number
python3 x12_278_qre_analyzer.py \
  ../../docs/examples/authorizations/inquiry/x215-inquiry-by-auth-number-request.edi

# Test query by member demographics
python3 x12_278_qre_analyzer.py \
  ../../docs/examples/authorizations/inquiry/x215-inquiry-by-member-request.edi

# Test error response
python3 x12_278_qre_analyzer.py \
  ../../docs/examples/authorizations/inquiry/x215-error-response.edi
```

## Troubleshooting

### Python Version Issues

```bash
# Check Python version
python3 --version

# If Python 3.7+ not available, install it
sudo apt-get update
sudo apt-get install python3.8
```

### Permission Denied

```bash
# Make script executable
chmod +x x12_278_qre_analyzer.py
chmod +x setup.sh
```

### Configuration Errors

```bash
# Validate configuration JSON
python3 -m json.tool qre-analyzer.config.json

# Use default configuration
rm qre-analyzer.config.json  # Will regenerate from schema
```

## Development

### Adding New Validation Rules

1. Update `qre-analyzer-config.schema.json` with new rule
2. Update `qre-analyzer.config.json` default configuration
3. Implement validation in `x12_278_qre_analyzer.py`
4. Add test cases for the new rule

### Schema Validation

```bash
# Validate config against schema (requires jsonschema)
pip install jsonschema
python3 -c "
import json
import jsonschema
with open('qre-analyzer-config.schema.json') as f:
    schema = json.load(f)
with open('qre-analyzer.config.json') as f:
    config = json.load(f)
jsonschema.validate(config, schema)
print('✓ Configuration is valid')
"
```

## References

- [X12 278 Healthcare Services Review TR3](https://store.x12.org/store/healthcare/healthcare-claim-005010)
- [Availity X12 278 X215 QRE Guide](https://www.availity.com/)
- [HIPAA X12 Implementation Guides](https://www.cms.gov/regulations-and-guidance/administrative-simplification/hipaa-aca/hipaa-transaction-and-code-set-standards)

## Support

For issues or questions:
1. Check the [Troubleshooting](#troubleshooting) section
2. Review validation codes in this README
3. Examine the JSON configuration options
4. Run with `--help` for usage information

## License

This analyzer is part of the HIPAA Attachments Logic Apps solution and follows the same licensing terms as the parent project.
