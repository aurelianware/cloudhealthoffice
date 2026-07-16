# ADR 003: pyx12 Library for X12 EDI Processing

## Status

**Accepted**

## Context

Cloud Health Office needs to parse and generate HIPAA X12 EDI transactions (275, 277, 278) without relying on Azure Integration Account. Options considered:

1. **pyx12** - Python X12 EDI library
2. **Edifact-Parser** - Generic EDI parsing library
3. **Custom Parser** - Build from scratch
4. **Commercial SDK** - Edifecs, Rhapsody, etc.

## Decision

We will use **pyx12** as the foundation for X12 parsing, supplemented with custom code for specific transaction types and encoding.

## Rationale

### pyx12 Advantages

1. **HIPAA-Focused**
   - Designed specifically for healthcare X12
   - Supports 005010 transaction sets
   - Validates against HIPAA implementation guides

2. **Permissively Licensed Dependency**
   - Apache 2.0 license
   - Active maintenance
   - Community contributions
   - No licensing costs

3. **Python Ecosystem**
   - Easy container integration
   - Extensive testing libraries
   - Fast development iteration

4. **Segment-Level Parsing**
   - Full ISA/GS/ST envelope handling
   - Segment and element extraction
   - Hierarchical loop navigation

### Comparison with Alternatives

| Feature | pyx12 | Custom Parser | Commercial |
|---------|-------|---------------|------------|
| HIPAA Support | ✅ Built-in | ❌ Manual | ✅ Full |
| License Cost | ✅ Free | ✅ Free | ❌ $$$ |
| Maintenance | ⚠️ Community | ❌ Internal | ✅ Vendor |
| Customization | ✅ Full | ✅ Full | ⚠️ Limited |
| Time to Implement | ✅ Fast | ❌ Slow | ✅ Fast |
| Compliance Cert | ❌ No | ❌ No | ✅ Often |

### Why Not Commercial SDK?

- High licensing costs ($50K-$200K/year)
- Vendor lock-in concerns
- Often overkill for our transaction types
- May not support containerization well

### Why Not Custom Parser?

- Complex X12 specification
- Error-prone implementation
- Long development time
- Ongoing maintenance burden

### Implementation Strategy

1. **Use pyx12 for parsing** (275, 278 inbound)
2. **Custom code for encoding** (277 outbound)
3. **Metadata extraction layer** on top of pyx12
4. **Validation against TR3 specs**

### Transaction Support

| Transaction | Parse | Encode | Status |
|-------------|-------|--------|--------|
| 275 (Attachment) | ✅ pyx12 | N/A | Complete |
| 277 (Status) | ✅ pyx12 | ✅ Custom | Complete |
| 278 (Auth) | ✅ pyx12 | N/A | Complete |
| 270/271 (Eligibility) | ✅ pyx12 | ✅ Custom | Planned |

## Consequences

### Positive

- No licensing costs
- Full control over implementation
- Easy to containerize
- Fast parsing performance
- Testable in isolation

### Negative

- Less comprehensive than commercial solutions
- No vendor support for edge cases
- Must maintain custom encoding code
- May need updates for X12 version changes

### Mitigations

- Extensive unit testing against sample files
- Document edge cases encountered
- Monitor pyx12 project for updates
- Consider commercial support for complex scenarios

## Implementation Details

### Parser Container

```python
# containers/x12-parser/parse_x12.py
from pyx12 import x12context

def parse_275(edi_content: str) -> dict:
    """Parse X12 275 attachment request"""
    ctx = x12context.X12Context()
    ctx.load_string(edi_content)
    
    # Extract segments
    isa = ctx.get_segment('ISA')
    gs = ctx.get_segment('GS')
    # ...
    
    return {
        'isa_envelope': isa,
        'gs_envelope': gs,
        'transaction_sets': [...]
    }
```

### Encoder Container

```python
# containers/x12-encoder/generate_277.py
def generate_277(claim_info: dict, envelope: dict) -> str:
    """Generate X12 277 RFAI response"""
    segments = []
    
    # Build ISA segment
    segments.append(build_isa(envelope))
    segments.append(build_gs(envelope))
    segments.append(build_st('277'))
    # ...
    
    return '~'.join(segments) + '~'
```

## Testing Strategy

1. **Unit Tests** - Parse/encode individual segments
2. **Integration Tests** - Full transaction round-trip
3. **Compliance Tests** - Validate against TR3 samples
4. **Regression Tests** - Production file samples (sanitized)

## References

- [pyx12 GitHub Repository](https://github.com/azoner/pyx12)
- [X12 005010 Implementation Guides](https://x12.org/products/transaction-sets)
- [HIPAA X12 Reference](https://www.cms.gov/Regulations-and-Guidance/Administrative-Simplification/HIPAA-ACA)
- [WEDI X12 Resources](https://www.wedi.org/knowledge-center/)
