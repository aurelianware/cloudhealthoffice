# Configuration Examples

This directory contains example configuration files demonstrating various Availity integration scenarios.

## ValueAdds277 Example

**File**: `valueadds277-example-config.json`

**Purpose**: Demonstrates a complete ECS module configuration with ValueAdds277 enhancement enabled.

**Key Features Demonstrated**:
- All four ECS query methods enabled (ServiceDate, Member, CheckNumber, ClaimHistory)
- ValueAdds277 enhancement fully enabled
- All claim field groups enabled (financial, remittance, clinical, demographics, statusDetails)
- Service line fields with financials and modifiers
- Integration flags for cross-module workflows (Appeals, Attachments, Corrections, Messaging, Remittance)
- Chat disabled (default for most payers)
- Caching disabled (recommended for real-time queries)

**Use Cases**:
1. **Baseline Configuration**: Start with this for new payer onboarding
2. **Testing**: Validate workflow parameter mapping
3. **Documentation**: Reference for configuration schema

**Customization**:
To customize for specific payers:
- Enable/disable field groups based on backend data availability
- Adjust integration flags based on enabled modules
- Enable chat if payer supports live chat
- Enable caching for high-volume scenarios

**Related Documentation**:
- [UNIFIED-CONFIG-SCHEMA.md](../../docs/UNIFIED-CONFIG-SCHEMA.md) - Complete schema reference
- [ECS-INTEGRATION.md](../../docs/ECS-INTEGRATION.md) - ECS module documentation
- [VALUEADDS277-README.md](../../docs/VALUEADDS277-README.md) - ValueAdds277 quick start

## Validation

To validate configuration files against the schema:

```bash
# Using Node.js with Ajv (if installed)
node -e "const Ajv = require('ajv'); const ajv = new Ajv(); const schema = require('./core/schemas/availity-integration-config.schema.json'); const config = require('./config/examples/valueadds277-example-config.json'); console.log(ajv.validate(schema, config) ? '✓ Valid' : '✗ Invalid');"

# Using jq for basic JSON validation
jq . config/examples/valueadds277-example-config.json > /dev/null && echo "✓ Valid JSON"
```

## Adding New Examples

When adding new example configurations:

1. Ensure they validate against the schema
2. Document the purpose and use case
3. Include comments explaining key configuration decisions
4. Test with the config-to-workflow generator (if applicable)
5. Update this README with the new example
