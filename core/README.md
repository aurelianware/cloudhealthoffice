# Unified Availity Integration Configuration

This directory contains the complete unified configuration schema for Availity health plan integrations.

## Quick Start

### 1. Create a Configuration File

```json
{
  "$schema": "../schemas/availity-integration-config.schema.json",
  "organizationName": "Your Health Plan",
  "payerId": "YOUR_PAYER_ID",
  "payerName": "Display Name",
  "contacts": {
    "technical": {
      "name": "John Smith",
      "email": "john.smith@example.com",
      "phone": "5551234567"
    },
    "accountManager": {
      "name": "Jane Doe",
      "email": "jane.doe@example.com"
    },
    "escalation": {
      "name": "Bob Johnson",
      "email": "bob.johnson@example.com"
    }
  },
  "modules": {
    "claims837": {
      "enabled": true,
      "transactionModes": {
        "realtime_web": {
          "enabled": true,
          "testUrl": "https://test-api.example.com/claims",
          "prodUrl": "https://api.example.com/claims"
        }
      },
      "claimTypes": {
        "professional": true,
        "institutional": true
      }
    }
  }
}
```

### 2. Validate the Configuration

#### Using TypeScript/JavaScript

```typescript
import { validateConfig } from './validation/config-validator';
import schema from './schemas/availity-integration-config.schema.json';
import config from './your-payer-config.json';

const result = validateConfig(config, schema);

if (result.valid) {
  console.log('✓ Configuration is valid');
} else {
  console.error('Configuration errors:', result.errors);
}
```

#### Using Node.js CLI

```javascript
const { ConfigValidator } = require('./validation/config-validator');
const fs = require('fs');

const schema = JSON.parse(fs.readFileSync('./schemas/availity-integration-config.schema.json', 'utf8'));
const config = JSON.parse(fs.readFileSync('./your-payer-config.json', 'utf8'));

const validator = new ConfigValidator(schema);
const result = validator.validate(config);

console.log(ConfigValidator.formatValidationResult(result));
```

### 3. Use in Your Application

```typescript
import { AvailityIntegrationConfig } from './interfaces/availity-integration-config.interface';

// Load configuration
const config: AvailityIntegrationConfig = loadConfig('payer-config.json');

// Access configuration values
const appealsEnabled = config.modules?.appeals?.enabled;
const appealsUrl = config.modules?.appeals?.transactionModes?.realtime_web?.prodUrl;
const maxFileSize = config.modules?.appeals?.maxFileSize;
```

## Directory Structure

```
core/
├── schemas/
│   └── availity-integration-config.schema.json  # JSON Schema Draft-07
├── interfaces/
│   └── availity-integration-config.interface.ts # TypeScript interfaces
├── examples/
│   ├── medicaid-mco-config.json                 # Example: Medicaid MCO
│   └── regional-blues-config.json               # Example: Regional BCBS
├── validation/
│   └── config-validator.ts                      # Configuration validator
└── README.md                                    # This file
```

## Schema Features

### Supported Modules

| Module | Description | Transaction Modes |
|--------|-------------|------------------|
| **claims837** | 837 Claims (Professional, Institutional, Dental) | Web, B2B, Batch |
| **eligibility270271** | 270/271 Eligibility verification | Web, B2B, Batch |
| **claimStatus276277** | 276/277 Claim status inquiries | Web, B2B, Batch |
| **appeals** | Appeals submission and tracking | Web, B2B |
| **attachments275** | 275 Clinical/administrative attachments | Web, B2B, Batch |
| **ecs** | Enhanced Claim Status with extended data | Web, B2B |

### Key Capabilities

- ✅ **Multi-Module Support** - Enable any combination of 6 transaction modules
- ✅ **Multi-Mode Support** - Real-time web, B2B, and EDI batch processing
- ✅ **Flexible Geography** - Nationwide or state-specific coverage
- ✅ **Contact Management** - Multiple contact types (technical, account manager, escalation)
- ✅ **Custom Edits** - Up to 8 custom claim edits per payer
- ✅ **Custom Enrichments** - Up to 5 custom data enrichments per payer
- ✅ **File Validation** - Configurable file types, sizes, and counts
- ✅ **Business Rules** - Built-in validation of business logic
- ✅ **Type Safety** - Complete TypeScript interfaces for IDE support

## Validation

The configuration validator performs:

1. **JSON Schema Validation** - Ensures structure matches the schema
2. **Format Validation** - Validates email addresses, URLs, phone numbers
3. **Pattern Validation** - Validates payer IDs, state codes, phone formats
4. **Range Validation** - Validates numeric constraints (file sizes, counts)
5. **Business Rule Validation** - Validates cross-field dependencies
6. **State Code Validation** - Validates US state/territory codes

### Example Validation Errors

```
Errors:
  [root] must have required property 'payerName'
  [geography.states] Invalid state code: XX. Must be a valid 2-letter US state code.
  [modules.appeals.maxAdditionalClaims] maxAdditionalClaims must be greater than 0 when additionalClaimsSupported is true

Warnings:
  [modules] No modules are enabled. At least one module should be enabled.
  [geography.states] Duplicate state codes found
```

## Examples

### Example 1: Medicaid MCO Configuration

**File**: `examples/medicaid-mco-config.json`

A nationwide Medicaid Managed Care Organization with:
- Appeals module (pre-appeal attachment pattern)
- Attachments 275 module (batch processing support)
- ECS module (all 4 query methods)
- Real-time web and B2B transaction modes

### Example 2: Regional BCBS Configuration

**File**: `examples/regional-blues-config.json`

A regional Blue Cross Blue Shield plan with:
- All 6 modules enabled
- Real-time web + EDI batch modes
- State-specific coverage (5 states)
- Custom claim edits and enrichments
- Post-appeal attachment pattern

## Documentation

📖 **[Complete Documentation](../docs/UNIFIED-CONFIG-SCHEMA.md)**

The comprehensive documentation includes:
- Complete field reference with descriptions
- Module-by-module configuration guide
- Validation rules and constraints
- Example configurations with explanations
- How to extend the schema for new modules
- Migration guide from hardcoded configurations

## TypeScript Integration

### Type Definitions

The interfaces provide full TypeScript support:

```typescript
import {
  AvailityIntegrationConfig,
  AppealsModule,
  Claims837Module,
  TransactionMode,
  Contact
} from './interfaces/availity-integration-config.interface';

// Create type-safe configuration
const config: AvailityIntegrationConfig = {
  organizationName: "My Health Plan",
  payerId: "12345",
  payerName: "My Payer",
  contacts: {
    technical: {
      name: "John Smith",
      email: "john@example.com"
    }
    // ... other contacts
  }
  // ... rest of config
};
```

### Enums

All enumerated values are available as TypeScript enums:

```typescript
import {
  AppealSubStatus,
  AppealDecision,
  EligibilitySearchOption,
  ECSQueryMethod,
  ConnectivityProtocol
} from './interfaces/availity-integration-config.interface';

// Use enums for type safety
const subStatus: AppealSubStatus = AppealSubStatus.IN_PROGRESS;
const searchOption: EligibilitySearchOption = EligibilitySearchOption.MEMBER_ID_DOB;
```

## Best Practices

### 1. Version Control

Store all configurations in version control:
```bash
git add core/configs/your-payer-config.json
git commit -m "Add configuration for Your Health Plan"
```

### 2. Environment Separation

Use separate files for test and production:
```
configs/
├── your-payer-test.json
└── your-payer-prod.json
```

### 3. Validation in CI/CD

Add validation to your CI/CD pipeline:
```yaml
- name: Validate configurations
  run: |
    npm install ajv ajv-formats
    node validate-all-configs.js
```

### 4. Configuration Reviews

Require code reviews for configuration changes:
- Verify all required fields are present
- Check URLs point to correct environments
- Validate contact information is current
- Ensure file size limits are appropriate

### 5. Documentation

Document payer-specific customizations:
```json
{
  "organizationName": "Special Payer",
  "modules": {
    "claims837": {
      "edits": [
        {
          "editId": "E001",
          "description": "Custom validation for Special Payer's unique requirements",
          "severity": "error"
        }
      ]
    }
  }
}
```

## Extending the Schema

To add new modules or fields:

1. Update the JSON Schema in `schemas/availity-integration-config.schema.json`
2. Update TypeScript interfaces in `interfaces/availity-integration-config.interface.ts`
3. Add validation rules in `validation/config-validator.ts`
4. Update documentation in `../docs/UNIFIED-CONFIG-SCHEMA.md`
5. Create example configurations demonstrating the new features

See the [Extension Guide](../docs/UNIFIED-CONFIG-SCHEMA.md#how-to-extend-the-schema) for detailed instructions.

## Support

For questions or issues:
1. Review the [complete documentation](../docs/UNIFIED-CONFIG-SCHEMA.md)
2. Check the [example configurations](examples/)
3. Validate your configuration using the validator
4. Contact the integration team

## License

This configuration schema is part of the Cloud Health Office Processing System and is licensed under the Apache License 2.0.
