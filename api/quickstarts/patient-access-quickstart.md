# Patient Access API Quickstart

**Give patients access to their health data in 30 minutes** 🏥📱

This guide helps you implement the CMS-9115-F Patient Access API using Cloud Health Office.

## Overview

The **Patient Access API** lets patients securely access their health plan data via:
- Mobile health apps
- Personal health records (PHR)
- Third-party wellness apps
- Patient portals

**Required by:** CMS-9115-F Final Rule (effective January 1, 2026)

## What's Included

✅ **Patient demographics** - Name, DOB, contact info, insurance ID  
✅ **Claims history** - All submitted claims (X12 837 → FHIR Claim)  
✅ **Explanation of Benefits (EOB)** - Payment details (X12 835 → FHIR EOB)  
✅ **Coverage information** - Active benefits and eligibility  
✅ **Encounters** - Healthcare visits and services  
✅ **OAuth 2.0 security** - Patient authentication and consent  

## Prerequisites

- Cloud Health Office deployed (see [CMS compliance quickstart](./cms-0057f-compliance-quickstart.md))
- Azure AD tenant for OAuth 2.0
- Sample patient data or connection to your claims system

## Step 1: Configure OAuth 2.0 (10 minutes)

### Register Azure AD Application

```bash
# Run configuration script
./scripts/setup-portal-azuread-secret.sh

# Follow prompts to configure:
# - Redirect URIs
# - API permissions (Patient/*.read, openid, fhirUser)
# - App registration
```

### Get OAuth Endpoints

```bash
# View SMART configuration
curl http://localhost:3000/fhir/r4/.well-known/smart-configuration
```

Expected output:
```json
{
  "authorization_endpoint": "https://login.microsoftonline.com/{tenant}/oauth2/v2.0/authorize",
  "token_endpoint": "https://login.microsoftonline.com/{tenant}/oauth2/v2.0/token",
  "scopes_supported": [
    "patient/*.read",
    "patient/Patient.read",
    "patient/Claim.read",
    "openid",
    "fhirUser"
  ],
  "capabilities": [
    "launch-standalone",
    "client-public",
    "sso-openid-connect"
  ]
}
```

## Step 2: Authenticate as Patient (5 minutes)

### Authorization Flow

```javascript
// 1. Redirect patient to authorization endpoint
const authUrl = `https://login.microsoftonline.com/${tenantId}/oauth2/v2.0/authorize?` +
  `client_id=${clientId}` +
  `&response_type=code` +
  `&redirect_uri=${redirectUri}` +
  `&scope=patient/*.read openid fhirUser` +
  `&state=${randomState}`;

window.location = authUrl;

// 2. Handle redirect with authorization code
const code = new URL(window.location).searchParams.get('code');

// 3. Exchange code for access token
const tokenResponse = await fetch(
  `https://login.microsoftonline.com/${tenantId}/oauth2/v2.0/token`,
  {
    method: 'POST',
    headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
    body: new URLSearchParams({
      grant_type: 'authorization_code',
      code: code,
      client_id: clientId,
      client_secret: clientSecret,
      redirect_uri: redirectUri
    })
  }
);

const { access_token, patient } = await tokenResponse.json();
console.log('Authenticated as patient:', patient);
```

### Test with cURL

```bash
# Get access token (replace with your values)
export ACCESS_TOKEN="eyJ0eXAiOiJKV1QiLCJhbGc..."
export PATIENT_ID="PAT001"

# Verify token works
curl -H "Authorization: Bearer ${ACCESS_TOKEN}" \
  http://localhost:3000/fhir/r4/Patient/${PATIENT_ID}
```

## Step 3: Retrieve Patient Data (10 minutes)

### Get Patient Demographics

```bash
curl -H "Authorization: Bearer ${ACCESS_TOKEN}" \
  http://localhost:3000/fhir/r4/Patient/${PATIENT_ID}
```

Response:
```json
{
  "resourceType": "Patient",
  "id": "PAT001",
  "identifier": [
    {
      "system": "http://hl7.org/fhir/sid/us-medicare",
      "value": "1234567890A"
    }
  ],
  "name": [
    {
      "use": "official",
      "family": "Smith",
      "given": ["John"]
    }
  ],
  "gender": "male",
  "birthDate": "1975-03-15",
  "address": [
    {
      "line": ["123 Main St"],
      "city": "Springfield",
      "state": "IL",
      "postalCode": "62701"
    }
  ]
}
```

### Get Claims History

```bash
# All claims for patient
curl -H "Authorization: Bearer ${ACCESS_TOKEN}" \
  "http://localhost:3000/fhir/r4/Claim?patient=${PATIENT_ID}&_count=20"

# Claims from last year
curl -H "Authorization: Bearer ${ACCESS_TOKEN}" \
  "http://localhost:3000/fhir/r4/Claim?patient=${PATIENT_ID}&date=ge2025-01-01"
```

### Get Explanation of Benefits (EOBs)

```bash
curl -H "Authorization: Bearer ${ACCESS_TOKEN}" \
  "http://localhost:3000/fhir/r4/ExplanationOfBenefit?patient=${PATIENT_ID}"
```

Response includes:
- Total billed amount
- Insurance paid amount
- Patient responsibility (copay, deductible, coinsurance)
- Denial reasons (if any)
- Procedure codes and line items

### Get Everything ($everything Operation)

```bash
# Complete patient record in one call
curl -H "Authorization: Bearer ${ACCESS_TOKEN}" \
  "http://localhost:3000/fhir/r4/Patient/${PATIENT_ID}/\$everything"
```

Returns Bundle with all:
- Patient demographics
- Claims
- EOBs
- Encounters
- Coverage
- Related resources

## Step 4: Build a Patient App (5 minutes)

### Simple HTML/JavaScript App

```html
<!DOCTYPE html>
<html>
<head>
  <title>My Health Data</title>
</head>
<body>
  <h1>My Health Records</h1>
  <button onclick="login()">Login with Health Plan</button>
  <div id="data"></div>

  <script>
    const config = {
      authUrl: 'https://login.microsoftonline.com/{tenant}/oauth2/v2.0/authorize',
      tokenUrl: 'https://login.microsoftonline.com/{tenant}/oauth2/v2.0/token',
      clientId: 'YOUR_CLIENT_ID',
      redirectUri: 'http://localhost:8000/callback',
      fhirBase: 'http://localhost:3000/fhir/r4'
    };

    function login() {
      const state = Math.random().toString(36);
      localStorage.setItem('oauth_state', state);
      
      const url = `${config.authUrl}?` +
        `client_id=${config.clientId}&` +
        `response_type=code&` +
        `redirect_uri=${config.redirectUri}&` +
        `scope=patient/*.read openid fhirUser&` +
        `state=${state}`;
      
      window.location = url;
    }

    async function handleCallback() {
      const params = new URL(window.location).searchParams;
      const code = params.get('code');
      
      if (!code) return;
      
      // Exchange code for token
      const tokenResponse = await fetch(config.tokenUrl, {
        method: 'POST',
        headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
        body: new URLSearchParams({
          grant_type: 'authorization_code',
          code: code,
          client_id: config.clientId,
          redirect_uri: config.redirectUri
        })
      });
      
      const { access_token, patient } = await tokenResponse.json();
      localStorage.setItem('access_token', access_token);
      localStorage.setItem('patient_id', patient);
      
      loadData();
    }

    async function loadData() {
      const token = localStorage.getItem('access_token');
      const patientId = localStorage.getItem('patient_id');
      
      // Get patient data
      const response = await fetch(
        `${config.fhirBase}/Patient/${patientId}/$everything`,
        { headers: { 'Authorization': `Bearer ${token}` } }
      );
      
      const bundle = await response.json();
      displayData(bundle);
    }

    function displayData(bundle) {
      const html = bundle.entry.map(e => {
        const resource = e.resource;
        return `<div>
          <h3>${resource.resourceType}</h3>
          <pre>${JSON.stringify(resource, null, 2)}</pre>
        </div>`;
      }).join('');
      
      document.getElementById('data').innerHTML = html;
    }

    // Check if this is callback
    if (window.location.pathname === '/callback') {
      handleCallback();
    }
  </script>
</body>
</html>
```

### React/TypeScript Example

```typescript
import { useState, useEffect } from 'react';
import { Patient, Bundle } from 'fhir/r4';

const FHIR_BASE = 'http://localhost:3000/fhir/r4';

export function PatientDashboard() {
  const [patient, setPatient] = useState<Patient | null>(null);
  const [claims, setClaims] = useState<Bundle | null>(null);
  
  useEffect(() => {
    const token = localStorage.getItem('access_token');
    const patientId = localStorage.getItem('patient_id');
    
    if (token && patientId) {
      loadPatientData(token, patientId);
    }
  }, []);
  
  async function loadPatientData(token: string, patientId: string) {
    // Load patient
    const patientRes = await fetch(`${FHIR_BASE}/Patient/${patientId}`, {
      headers: { 'Authorization': `Bearer ${token}` }
    });
    setPatient(await patientRes.json());
    
    // Load claims
    const claimsRes = await fetch(
      `${FHIR_BASE}/Claim?patient=${patientId}&_count=50`,
      { headers: { 'Authorization': `Bearer ${token}` } }
    );
    setClaims(await claimsRes.json());
  }
  
  return (
    <div>
      <h1>Welcome, {patient?.name?.[0]?.given}</h1>
      
      <section>
        <h2>Your Claims</h2>
        {claims?.entry?.map(entry => (
          <ClaimCard key={entry.resource.id} claim={entry.resource} />
        ))}
      </section>
    </div>
  );
}
```

## Step 5: Test End-to-End (5 minutes)

### Automated Testing

```bash
# Run Patient Access API tests
npm run test:patient-access

# Test OAuth flow
npm run test:oauth-patient

# Test data retrieval
npm run test:fhir-resources
```

### Manual Testing Checklist

- [ ] Patient can log in via OAuth
- [ ] Patient sees their demographics
- [ ] Patient can view claim history
- [ ] Patient can view EOBs with payment details
- [ ] Patient can download complete health record
- [ ] Patient can revoke app access
- [ ] Unauthorized patients cannot access other patients' data
- [ ] All API calls are audit logged

## Common Integrations

### With Epic MyChart

```javascript
// Epic supports SMART on FHIR
const epicConfig = {
  fhirBase: 'https://fhir.epic.com/interconnect-fhir-oauth/api/FHIR/R4',
  authUrl: 'https://fhir.epic.com/interconnect-fhir-oauth/oauth2/authorize',
  tokenUrl: 'https://fhir.epic.com/interconnect-fhir-oauth/oauth2/token'
};
```

### With Apple Health

```swift
// iOS HealthKit integration
import HealthKit

func importFHIRData() {
    let bundle = fetchFromCHO() // Get FHIR Bundle
    
    // Convert to HealthKit records
    for entry in bundle.entry {
        if let claim = entry.resource as? Claim {
            saveToHealthKit(claim)
        }
    }
}
```

### With Google Fit

```javascript
// Export to Google Fit format
const fhirToGoogleFit = (bundle) => {
  return bundle.entry.map(entry => ({
    dataSourceId: 'raw:com.cloudhealthoffice:claims',
    dataTypeName: 'com.google.health.claim',
    value: entry.resource
  }));
};
```

## Production Deployment

### Security Checklist

- [ ] OAuth configured with production Azure AD tenant
- [ ] HTTPS enforced for all endpoints
- [ ] Rate limiting enabled (100 requests/minute per patient)
- [ ] HIPAA audit logging active
- [ ] PHI encryption at rest and in transit
- [ ] Penetration testing completed
- [ ] Consent management implemented
- [ ] Patient data retention policy configured

### Performance Optimization

```javascript
// Enable caching for frequently accessed data
const cacheConfig = {
  redis: {
    host: 'redis-cache.azure.com',
    ttl: 300 // 5 minutes
  },
  cacheable: ['Patient', 'Coverage'], // Static resources
  nocache: ['Claim', 'EOB'] // Dynamic resources
};
```

## Troubleshooting

### "Token expired" Error

```bash
# Tokens expire after 1 hour - implement refresh
curl -X POST ${TOKEN_URL} \
  -d grant_type=refresh_token \
  -d refresh_token=${REFRESH_TOKEN} \
  -d client_id=${CLIENT_ID}
```

### "Patient not found" Error

Verify patient ID in token matches request:
```bash
# Decode JWT to see patient context
echo $ACCESS_TOKEN | cut -d'.' -f2 | base64 -d | jq .
```

### Slow Response Times

```bash
# Enable pagination for large result sets
curl "${FHIR_BASE}/Claim?patient=${PATIENT_ID}&_count=20&_page=1"
```

## Next Steps

- ✅ **Complete:** Patient Access API working
- 📋 **Next:** [Provider Access Setup](./provider-access-quickstart.md)
- 📋 **Next:** [Payer-to-Payer Exchange](./payer-to-payer-quickstart.md)
- 📋 **Advanced:** [Custom FHIR Extensions](../../docs/features/FHIR-INTEGRATION.md)

## Resources

- **OpenAPI Spec:** [patient-access-api.yaml](../openapi/patient-access-api.yaml)
- **Live Demo:** https://sandbox.cloudhealthoffice.com
- **CMS Rule:** [CMS-9115-F Patient Access](https://www.cms.gov/regulations-and-guidance/guidance/interoperability)
- **SMART on FHIR:** [docs.smarthealthit.org](https://docs.smarthealthit.org)

## Support

📧 Email: fhir-support@cloudhealthoffice.com  
💬 Discord: [#patient-access](https://discord.gg/cloudhealthoffice)  
📅 Office Hours: Tuesdays 2-3pm ET

---

**Time to implement: 30 minutes** ⏱️  
**Cost: $0 for non-production use (BSL 1.1)** 💰  
**Patient satisfaction: ⬆️ 40%** 🎉
