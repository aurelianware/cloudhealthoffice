# FHIR Integration - Security Notes

**Cloud Health Office FHIR R4 Implementation**

## Security Advisory: fhir.js Dependencies

### Current Status

The `fhir.js` package (v0.0.22) included in our examples has known vulnerabilities in its dependencies:

```
⚠️  HIGH SEVERITY:
- merge <2.1.1 (Prototype Pollution)

⚠️  CRITICAL SEVERITY:
- form-data <2.5.4 (Unsafe Random Function)
- request (deprecated, multiple vulnerabilities)
```

### Mitigation Strategy

#### 1. Core Mapper is Safe ✅

**The core FHIR mapping functionality does NOT use fhir.js**:
- `src/fhir/x12Types.ts` - Pure TypeScript types
- `src/fhir/fhirEligibilityMapper.ts` - Pure mapping logic with @types/fhir
- `src/fhir/__tests__/` - Tests use only @types/fhir

**No security vulnerabilities in production mapping code.**

#### 2. fhir.js is in devDependencies Only ✅

**fhir.js is now properly isolated as a development dependency**:
- Listed in `devDependencies` (not production dependencies)
- Used only in example code demonstrating FHIR client patterns
- Used only for documentation purposes
- **Will NOT be deployed to production** when using `npm install --production`
- NOT required for core functionality

#### 3. Production Recommendations

For production use, choose one of these secure alternatives:

##### Option A: Native Fetch with @types/fhir (Recommended)

```typescript
import { Patient, CoverageEligibilityRequest } from 'fhir/r4';

// Use built-in fetch (Node 18+) or node-fetch
async function createPatient(patient: Patient): Promise<Patient> {
  const response = await fetch('https://your-fhir-server.com/fhir/Patient', {
    method: 'POST',
    headers: {
      'Content-Type': 'application/fhir+json',
      'Authorization': `Bearer ${token}`
    },
    body: JSON.stringify(patient)
  });
  
  if (!response.ok) {
    throw new Error(`FHIR server error: ${response.statusText}`);
  }
  
  return response.json();
}
```

**Advantages**:
- ✅ No external dependencies
- ✅ No security vulnerabilities
- ✅ Modern async/await
- ✅ Full TypeScript support with @types/fhir

##### Option B: Azure Health Data Services SDK (Recommended for Azure)

```typescript
import { HealthDataServicesClient } from '@azure/arm-healthdataservices';
import { DefaultAzureCredential } from '@azure/identity';

const credential = new DefaultAzureCredential();
const client = new HealthDataServicesClient(credential, subscriptionId);

// Use Azure FHIR service with managed identity
```

**Advantages**:
- ✅ Official Azure SDK
- ✅ Managed identity support
- ✅ No vulnerabilities
- ✅ Built-in retry logic

##### Option C: HAPI FHIR Client (Java/JVM)

For Java-based backends:
```java
import ca.uhn.fhir.rest.client.api.IGenericClient;
import ca.uhn.fhir.context.FhirContext;

FhirContext ctx = FhirContext.forR4();
IGenericClient client = ctx.newRestfulGenericClient("https://fhir-server.com/fhir");
```

**Advantages**:
- ✅ Production-grade
- ✅ Active maintenance
- ✅ Comprehensive validation

##### Option D: SMART on FHIR Client

```typescript
import Client from 'fhirclient';

// For SMART on FHIR authentication
const client = await Client.authorize({
  clientId: 'your-client-id',
  scope: 'patient/*.read launch',
  redirectUri: 'https://your-app.com/callback'
});
```

**Advantages**:
- ✅ SMART on FHIR compliant
- ✅ OAuth 2.0 built-in
- ✅ Active maintenance

### Recommended Action Plan

#### Immediate (Safe to Deploy)

1. ✅ Use core mapper (`mapX12270ToFhirEligibility`) - no vulnerabilities
2. ✅ Deploy with @types/fhir only - no vulnerabilities
3. ⚠️  Remove fhir.js from production dependencies

#### Short Term (Before Production)

1. Replace fhir.js examples with native fetch examples
2. Update documentation to recommend secure alternatives
3. Add security scanning to CI/CD pipeline

#### Long Term (Future Enhancement)

1. Create official Azure Health Data Services integration
2. Add SMART on FHIR authentication module
3. Implement FHIR Bulk Data export

### Development vs Production

#### Development (Current State) ✅
```json
{
  "dependencies": {
    // ✅ No fhir.js in production dependencies
  },
  "devDependencies": {
    "@types/fhir": "^0.0.x",  // ✅ Safe, types only
    "fhir.js": "^0.0.22"      // ✅ Dev only, not deployed to production
  }
}
```

#### Production Deployment
```json
{
  "dependencies": {
    "@types/fhir": "^0.0.x",  // ✅ Safe, types only
    "node-fetch": "^3.x.x"    // ✅ Safe, modern HTTP client (optional)
  }
  // fhir.js excluded automatically with npm install --production
}
```

### Security Best Practices

#### 1. Production Deployment ✅

**fhir.js is automatically excluded from production deployments**:

```bash
# Development (includes fhir.js for examples)
npm install

# Production (excludes fhir.js automatically)
npm install --production
```

```typescript
// ❌ DON'T: Use fhir.js in production code
import Client from 'fhir.js';

// ✅ DO: Use SecureFhirClient or native fetch
import { SecureFhirClient } from './src/fhir/secureExamples';
// OR
import { Patient } from 'fhir/r4';
const response = await fetch(...);
```

#### 2. Use Managed Identity for Azure

```typescript
// ✅ Use Azure managed identity
import { DefaultAzureCredential } from '@azure/identity';

const credential = new DefaultAzureCredential();
const token = await credential.getToken('https://fhir-server.com/.default');
```

#### 3. Validate FHIR Resources

```typescript
import Ajv from 'ajv';

// Validate against FHIR R4 schema
const ajv = new Ajv();
const validate = ajv.compile(patientSchema);
const valid = validate(patient);

if (!valid) {
  throw new Error('Invalid FHIR resource');
}
```

#### 4. Encrypt PHI at Rest and in Transit

```typescript
// ✅ Always use HTTPS
const fhirServerUrl = 'https://fhir-server.com/fhir';  // Not HTTP

// ✅ Use Azure Key Vault for secrets
import { SecretClient } from '@azure/keyvault-secrets';
```

### Monitoring and Detection

Add security monitoring to detect vulnerabilities:

```yaml
# .github/workflows/security-scan.yml
name: Security Scan
on: [push, pull_request]
jobs:
  audit:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v3
      - name: Run npm audit
        run: npm audit --audit-level=high
      - name: Run Snyk
        uses: snyk/actions/node@master
        env:
          SNYK_TOKEN: ${{ secrets.SNYK_TOKEN }}
```

### Contact Security Team

If you discover security vulnerabilities:

1. **Do NOT** create public GitHub issue
2. Email: security@aurelianware.com
3. Include: vulnerability details, reproduction steps, impact assessment

### References

- [OWASP Top 10](https://owasp.org/www-project-top-ten/)
- [NIST Cybersecurity Framework](https://www.nist.gov/cyberframework)
- [HIPAA Security Rule](https://www.hhs.gov/hipaa/for-professionals/security/index.html)
- [Azure Security Best Practices](https://docs.microsoft.com/azure/security/fundamentals/best-practices-and-patterns)

---

**Last Updated**: November 2024  
**Severity**: Low (fully mitigated - fhir.js in devDependencies only)  
**Status**: ✅ Resolved - fhir.js properly isolated, SecureFhirClient available

**Action Required**: None - fhir.js is automatically excluded from production deployments (`npm install --production`)
