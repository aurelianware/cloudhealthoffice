---
name: "[v4.0] Real Clearinghouse Integration"
about: Integrate Availity, Change Healthcare, and Optum for production EDI exchanges
title: "[v4.0] Integrate Real Clearinghouses (Availity, Change Healthcare, Optum)"
labels: enhancement, integration, v4.0, priority-high
assignees: aurelianware
---

## Overview
Enable production EDI exchanges with major clearinghouses to process real X12 transactions (837 claims, 270 eligibility, 278 prior auth). Build on existing SFTP automation and X12 parsing, integrating tenant management for payer-specific configs and Stripe for transaction-based billing.

## Objectives
- ✅ Integrate with 3 major clearinghouses (Availity, Change Healthcare, Optum)
- ✅ Process real X12 transactions (837/270/278/835/837)
- ✅ Achieve <500ms adjudication SLA
- ✅ Enable usage-based billing (per-transaction metering)
- ✅ Maintain HIPAA compliance (PHI encryption, audit trails)

## Clearinghouse Partners

### 1. Availity
- **Market Share:** 35% (largest US clearinghouse)
- **API:** SOAP + REST (eligibility), SFTP (claims)
- **Supported Transactions:** 270/271, 276/277, 278/279, 837, 835
- **Test Environment:** https://apps.availity.com/availity/web/public.elegant.login
- **Integration:** Real-time API for eligibility, batch SFTP for claims

### 2. Change Healthcare (Optum)
- **Market Share:** 30%
- **API:** REST API (Intelligent Healthcare Network)
- **Supported Transactions:** All standard X12 (270-835)
- **Test Environment:** https://developers.changehealthcare.com
- **Integration:** RESTful JSON (auto-convert to/from X12)

### 3. Optum (UnitedHealth Group)
- **Market Share:** 25%
- **API:** Optum One Platform (REST)
- **Supported Transactions:** All X12 + proprietary formats
- **Test Environment:** Contact for sandbox access
- **Integration:** SFTP + REST hybrid

## Implementation Steps

### Phase 1: Clearinghouse Adapter Service (Week 1-2)

**Goal:** Create unified adapter layer for all clearinghouses

**Tasks:**
- [ ] Create new microservice: `services/clearinghouse-adapter-service/`
  ```
  services/clearinghouse-adapter-service/
  ├── Adapters/
  │   ├── IAvailityAdapter.cs
  │   ├── AvailityAdapter.cs (SOAP client)
  │   ├── IChangeHealthcareAdapter.cs
  │   ├── ChangeHealthcareAdapter.cs (REST client)
  │   ├── IOptumAdapter.cs
  │   └── OptumAdapter.cs (hybrid)
  ├── Models/
  │   ├── ClearinghouseRequest.cs
  │   ├── ClearinghouseResponse.cs
  │   └── TransactionLog.cs
  ├── Controllers/
  │   └── ClearinghouseController.cs
  └── Services/
      ├── IRoutingService.cs (route to correct clearinghouse)
      └── RoutingService.cs
  ```

- [ ] Implement adapter pattern for abstraction:
  ```csharp
  public interface IClearinghouseAdapter
  {
      Task<EligibilityResponse> CheckEligibilityAsync(string x12_270, string tenantId);
      Task<string> SubmitClaimAsync(string x12_837, string tenantId);
      Task<ClaimStatusResponse> CheckClaimStatusAsync(string claimId);
      Task<Remittance> GetRemittanceAsync(string remittanceId);
  }
  ```

- [ ] Add tenant-to-clearinghouse routing in Tenant model:
  ```csharp
  // In Tenant.cs
  public ClearinghouseConfig Clearinghouse { get; set; }
  
  public class ClearinghouseConfig
  {
      public string Provider { get; set; } // "availity" | "changehealthcare" | "optum"
      public string SenderId { get; set; }
      public string ReceiverId { get; set; }
      public string SubmitterName { get; set; }
      public Dictionary<string, string> Credentials { get; set; } // From Key Vault
  }
  ```

### Phase 2: Availity Integration (Week 2-3)

**Tasks:**
- [ ] Register for Availity Developer Portal
- [ ] Obtain test credentials (Sender ID, Password)
- [ ] Implement SOAP client for Eligibility API:
  ```csharp
  public class AvailityAdapter : IClearinghouseAdapter
  {
      private readonly SoapClient _client;
      
      public async Task<EligibilityResponse> CheckEligibilityAsync(string x12_270, string tenantId)
      {
          var request = new AvailityEligibilityRequest
          {
              SenderId = _config.SenderId,
              ReceiverId = "AVAILITY",
              Transaction = x12_270
          };
          var response = await _client.SubmitAsync(request);
          return ParseX12_271(response.Transaction);
      }
  }
  ```

- [ ] Configure SFTP for batch claims (837):
  - Hostname: `sftp.availity.com`
  - Inbound: `/claims/inbound/{senderId}/`
  - Outbound: `/claims/outbound/{senderId}/`
  - Auth: Key pair (store in Key Vault)

- [ ] Update Argo Workflow `x12-837-ingest.yaml`:
  ```yaml
  - name: submit-to-availity
    container:
      image: ghcr.io/aurelianware/cloudhealthoffice-sftp-publisher:latest
      env:
        - name: SFTP_HOST
          value: "sftp.availity.com"
        - name: SFTP_USERNAME
          valueFrom:
            secretKeyRef:
              name: availity-creds
              key: username
  ```

### Phase 3: Change Healthcare Integration (Week 3-4)

**Tasks:**
- [ ] Register at https://developers.changehealthcare.com
- [ ] Obtain OAuth2 client credentials
- [ ] Implement REST client:
  ```csharp
  public class ChangeHealthcareAdapter : IClearinghouseAdapter
  {
      private readonly HttpClient _http;
      
      public async Task<EligibilityResponse> CheckEligibilityAsync(string x12_270, string tenantId)
      {
          // Convert X12 to JSON
          var json = X12ToJsonConverter.Convert(x12_270);
          
          var response = await _http.PostAsync(
              "https://api.changehealthcare.com/eligibility/v1/check",
              new StringContent(json, Encoding.UTF8, "application/json")
          );
          
          var result = await response.Content.ReadAsStringAsync();
          return JsonToX12Converter.ConvertEligibilityResponse(result);
      }
  }
  ```

- [ ] Add X12 ↔ JSON converters using `X12.NET` library
- [ ] Configure OAuth2 token refresh (store in Key Vault)

### Phase 4: Optum Integration (Week 4-5)

**Tasks:**
- [ ] Contact Optum for sandbox credentials
- [ ] Implement hybrid adapter (REST for eligibility, SFTP for claims)
- [ ] Add Optum-specific validation (stricter NPI requirements)

### Phase 5: Event-Driven Processing (Week 5-6)

**Goal:** Enhance Argo Workflows for clearinghouse automation

**Tasks:**
- [ ] Update Kafka topics for clearinghouse events:
  ```yaml
  # kafka/topics.yaml
  - name: clearinghouse-outbound
    partitions: 6
    replication: 3
    config:
      retention.ms: 604800000  # 7 days
  
  - name: clearinghouse-inbound
    partitions: 6
    replication: 3
    config:
      retention.ms: 2592000000  # 30 days
  ```

- [ ] Create Argo EventSource for SFTP polling:
  ```yaml
  # argo-events/clearinghouse-eventsource.yaml
  apiVersion: argoproj.io/v1alpha1
  kind: EventSource
  metadata:
    name: clearinghouse-sftp-poller
  spec:
    generic:
      sftp-availity:
        url: sftp://sftp.availity.com/claims/outbound/BSCA123/
        interval: 60s  # Poll every minute
  ```

- [ ] Create Argo Sensor for processing inbound files:
  ```yaml
  # argo-events/clearinghouse-sensor.yaml
  apiVersion: argoproj.io/v1alpha1
  kind: Sensor
  metadata:
    name: clearinghouse-response-processor
  spec:
    triggers:
    - template:
        name: process-835-remittance
        argoWorkflow:
          source:
            resource:
              apiVersion: argoproj.io/v1alpha1
              kind: Workflow
              metadata:
                generateName: process-835-
              spec:
                entrypoint: parse-and-update
                templates:
                - name: parse-and-update
                  steps:
                  - - name: parse-835
                      template: x12-parser
                  - - name: update-claims
                      template: claims-updater
  ```

- [ ] Add retry logic with exponential backoff:
  ```csharp
  var policy = Policy
      .Handle<HttpRequestException>()
      .WaitAndRetryAsync(3, retryAttempt => 
          TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)));
  
  await policy.ExecuteAsync(async () => 
      await _clearinghouseAdapter.SubmitClaimAsync(x12, tenantId));
  ```

### Phase 6: Stripe Billing Integration (Week 6)

**Goal:** Charge per transaction with Stripe metering

**Tasks:**
- [ ] Create Stripe meter for transaction volume:
  ```bash
  stripe meters create \
    --display-name "EDI Transactions" \
    --event-name "transaction.processed"
  ```

- [ ] Update `TenantService.UpdateUsageAsync()`:
  ```csharp
  public async Task UpdateUsageAsync(string tenantId, string metricType, int count)
  {
      // Update Cosmos DB usage
      await _repository.UpdateUsageAsync(tenantId, metricType, count);
      
      // Report to Stripe for billing
      if (metricType == "ClaimsThisMonth")
      {
          await _stripeService.ReportUsageAsync(tenantId, "transaction.processed", count);
      }
  }
  ```

- [ ] Add usage-based pricing tier in Stripe:
  - **Starter:** Contact sales for pricing
  - **Professional:** Contact sales for pricing
  - **Enterprise:** Contact sales for pricing

- [ ] Emit Kafka event on transaction complete:
  ```csharp
  await _kafka.ProduceAsync("usage-events", new UsageEvent
  {
      TenantId = tenantId,
      EventType = "claim_submitted",
      Timestamp = DateTime.UtcNow,
      Metadata = new { ClaimId = claimId, Clearinghouse = "availity" }
  });
  ```

### Phase 7: Compliance & Monitoring (Week 7)

**Tasks:**
- [ ] Add PHI masking in logs:
  ```csharp
  logger.LogInformation("Submitted claim {ClaimId} to {Clearinghouse} for tenant {TenantId}", 
      claimId, clearinghouse, tenantId);
  // Never log full X12 content or SSNs
  ```

- [ ] Configure Prometheus metrics:
  ```csharp
  private static readonly Counter TransactionsProcessed = Metrics
      .CreateCounter("clearinghouse_transactions_total", "Total transactions",
          new CounterConfiguration { LabelNames = new[] { "clearinghouse", "type", "status" } });
  
  TransactionsProcessed.WithLabels("availity", "837", "success").Inc();
  ```

- [ ] Build Grafana dashboard:
  - Transaction volume by clearinghouse
  - Avg response time (<500ms SLA)
  - Error rate by type (timeout, validation, rejection)
  - Cost per transaction (Stripe billing)

- [ ] Set up alerts:
  - Clearinghouse API down (>5 consecutive failures)
  - Response time >1s (SLA breach)
  - Error rate >5%

## Testing

### Unit Tests
```csharp
[Fact]
public async Task AvailityAdapter_CheckEligibility_ReturnsValid271()
{
    var x12_270 = LoadTestFile("eligibility_request.edi");
    var response = await _availityAdapter.CheckEligibilityAsync(x12_270, "test-tenant");
    Assert.Equal("ACTIVE", response.BenefitStatus);
}
```

### Integration Tests
- [ ] Test with clearinghouse sandbox accounts
- [ ] Validate X12 compliance with sample files from partners
- [ ] E2E: Submit 837 → receive 277 (claim acknowledged) → receive 835 (remittance)
- [ ] Load test: 10,000 claims/hour (simulate large payer)

### Performance Tests
- [ ] Measure adjudication time: parse X12 → route to clearinghouse → receive response
  - **Target:** <500ms for eligibility, <5s for claims
- [ ] Kafka throughput: sustain 1,000 messages/sec

## Dependencies
- ✅ Tenant Management Service (routing configs)
- ✅ Stripe Billing (metering)
- ⏳ Security Hardening (Key Vault for credentials)
- ⏳ Clearinghouse test accounts (Availity, Change, Optum)

## Documentation
- [ ] Create [docs/CLEARINGHOUSE-INTEGRATION.md](../../docs/CLEARINGHOUSE-INTEGRATION.md)
- [ ] Update [DEPLOYMENT.md](../../DEPLOYMENT.md) with onboarding guide:
  1. Register with clearinghouse
  2. Add credentials to Key Vault
  3. Update tenant config with Sender ID
  4. Test with sample X12 files
- [ ] Add API examples to Swagger docs

## Success Criteria
- ✅ All 3 clearinghouses connected and tested
- ✅ 99.9% uptime for clearinghouse API calls
- ✅ <500ms median response time for eligibility
- ✅ Zero PHI leaks in logs (automated scans pass)
- ✅ Stripe metering tracks 100% of transactions
- ✅ E2E workflow: 837 submission → 835 remittance in <30 seconds

## Timeline
- **Weeks 1-2:** Adapter service + Availity integration
- **Weeks 3-4:** Change Healthcare integration
- **Weeks 4-5:** Optum integration
- **Weeks 5-6:** Event-driven processing + Stripe metering
- **Week 7:** Compliance, monitoring, testing

**Total:** 7 weeks (2 FTE)

## References
- [Availity Developer Portal](https://www.availity.com/essentials)
- [Change Healthcare API Docs](https://developers.changehealthcare.com)
- [X12 Standards](https://x12.org/products/standards)
- [HIPAA 5010 Implementation Guides](https://www.cms.gov/regulations-and-guidance/administrative-simplification/hipaa-aca)
