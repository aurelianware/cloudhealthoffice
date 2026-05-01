# Cloud Health Office - Monitoring & Observability Strategy

## Overview

Complete monitoring stack for Cloud Health Office microservices platform using Prometheus + Grafana on Kubernetes.

## Architecture

```
┌─────────────────────────────────────────────────────────────┐
│  Grafana Dashboards (cho-monitoring:3000)                   │
│  • EDI Workflows Dashboard                                  │
│  • Microservices Health Dashboard                           │
│  • Claims Adjudication Metrics                              │
│  • AKS Cluster Overview                                     │
└────────────┬────────────────────────────────────────────────┘
             │ Queries
             ▼
┌─────────────────────────────────────────────────────────────┐
│  Prometheus (cho-monitoring:9090)                           │
│  • Scrapes metrics from all pods                            │
│  • Stores time-series data (15 days retention)              │
│  • AlertManager integration                                 │
└────────────┬────────────────────────────────────────────────┘
             │ Scrapes metrics every 15s
             │
    ┌────────┼────────┬────────┬────────┬────────┐
    │        │        │        │        │        │
    ▼        ▼        ▼        ▼        ▼        ▼
┌─────────┐┌─────────┐┌─────────┐┌─────────┐┌─────────┐┌─────────┐
│EDI Jobs ││Eligib.  ││Benefit  ││Provider ││Ref Data ││SFTP     │
│275/277/ ││Service  ││Service  ││Service  ││Service  ││Server   │
│278/837  ││:3000    ││:3001    ││:3002    ││:3003    ││:22      │
└─────────┘└─────────┘└─────────┘└─────────┘└─────────┘└─────────┘
```

## Quick Start - Deploy Monitoring Stack

```bash
# Add Prometheus Helm repo
helm repo add prometheus-community https://prometheus-community.github.io/helm-charts
helm repo update

# Create monitoring namespace
kubectl create namespace cho-monitoring

# Install Prometheus + Grafana stack (30 seconds!)
helm install cho-monitoring prometheus-community/kube-prometheus-stack \
  --namespace cho-monitoring \
  --set prometheus.prometheusSpec.retention=15d \
  --set prometheus.prometheusSpec.storageSpec.volumeClaimTemplate.spec.resources.requests.storage=50Gi \
  --set grafana.adminPassword=CloudHealthOffice2026 \
  --set grafana.persistence.enabled=true \
  --set grafana.persistence.size=10Gi

# Access Grafana
kubectl port-forward -n cho-monitoring svc/cho-monitoring-grafana 3000:80
```

**Grafana URL**: http://localhost:3000  
**Login**: admin / CloudHealthOffice2026

## Key Dashboards to Import

### 1. EDI Workflows Dashboard
- Job execution timeline
- Success/failure rates (275/277/278/837)
- Files processed per hour
- SFTP connectivity

### 2. Claims Adjudication Dashboard (Future)
```
Claim Processing Pipeline:
┌─────────────┐  ┌──────────┐  ┌─────────┐  ┌──────────┐  ┌─────────────┐
│ Eligibility │→ │ Provider │→ │ Benefit │→ │ Scrubbing│→ │ Adjudication│
│   50ms      │  │   30ms   │  │  40ms   │  │   60ms   │  │    100ms    │
└─────────────┘  └──────────┘  └─────────┘  └──────────┘  └─────────────┘
     ✅ 99.8%        ✅ 99.9%     ✅ 99.5%      ✅ 98.2%         ✅ 95.1%
```

## Claims Adjudication Argo Workflow (Preview)

Once all microservices are deployed:

```yaml
apiVersion: argoproj.io/v1alpha1
kind: Workflow
metadata:
  name: claim-adjudication
  namespace: cho-workflows
spec:
  entrypoint: adjudicate-claim
  arguments:
    parameters:
      - name: claim-id
        value: "CLM-2026-001"
      - name: patient-id
        value: "P123456"
      - name: provider-npi
        value: "1234567890"
  
  templates:
    - name: adjudicate-claim
      dag:
        tasks:
          # Step 1: Verify patient eligibility
          - name: check-eligibility
            template: http-call
            arguments:
              parameters:
                - name: url
                  value: "http://eligibility-service.cloudhealthoffice:3000/api/v1/eligibility/{{workflow.parameters.patient-id}}"
                - name: method
                  value: "GET"
          
          # Step 2: Verify provider is in-network
          - name: verify-provider
            dependencies: [check-eligibility]
            template: http-call
            arguments:
              parameters:
                - name: url
                  value: "http://provider-service.cloudhealthoffice:3002/api/v1/providers/{{workflow.parameters.provider-npi}}"
                - name: method
                  value: "GET"
          
          # Step 3: Get benefit plan details
          - name: get-benefits
            dependencies: [check-eligibility]
            template: http-call
            arguments:
              parameters:
                - name: url
                  value: "http://benefit-service.cloudhealthoffice:3001/api/v1/plans/{{tasks.check-eligibility.outputs.parameters.plan-id}}"
                - name: method
                  value: "GET"
          
          # Step 4: Validate CPT codes
          - name: validate-codes
            template: http-call
            arguments:
              parameters:
                - name: url
                  value: "http://reference-data-service.cloudhealthoffice:3003/api/v1/codes/validate"
                - name: method
                  value: "POST"
                - name: body
                  value: '{"cpt_codes": ["99213", "99214"]}'
          
          # Pre-adjudication scrubbing was a separate HTTP call against
          # claims-scrubbing-service in earlier revisions of this
          # workflow. As of capability 5.4 it runs in-process inside
          # claims-service via the CloudHealthOffice.ClaimsScrubEngine
          # class library at adjudication pipeline Order=100, so the
          # adjudication step calls a single endpoint that scrubs and
          # adjudicates atomically.

          # Step 5: Run adjudication engine (scrubbing happens in-process)
          - name: adjudicate
            dependencies: [verify-provider, get-benefits]
            template: adjudication-engine
            arguments:
              parameters:
                - name: eligibility-result
                  value: "{{tasks.check-eligibility.outputs.result}}"
                - name: provider-result
                  value: "{{tasks.verify-provider.outputs.result}}"
                - name: benefit-result
                  value: "{{tasks.get-benefits.outputs.result}}"
          
          # Step 6: Generate EOB (Explanation of Benefits)
          - name: generate-eob
            dependencies: [adjudicate]
            template: generate-eob
            arguments:
              parameters:
                - name: adjudication-result
                  value: "{{tasks.adjudicate.outputs.result}}"
    
    # Reusable HTTP call template
    - name: http-call
      inputs:
        parameters:
          - name: url
          - name: method
          - name: body
            value: ""
      script:
        image: curlimages/curl:latest
        command: [sh]
        source: |
          curl -X {{inputs.parameters.method}} \
            -H "Content-Type: application/json" \
            {{inputs.parameters.url}} \
            {{#if inputs.parameters.body}}-d '{{inputs.parameters.body}}'{{/if}}
    
    # Adjudication logic
    - name: adjudication-engine
      inputs:
        parameters:
          - name: eligibility-result
          - name: provider-result
          - name: benefit-result
      container:
        image: acr.azurecr.io/cho/adjudication-engine:latest
        env:
          - name: ELIGIBILITY_DATA
            value: "{{inputs.parameters.eligibility-result}}"
          - name: PROVIDER_DATA
            value: "{{inputs.parameters.provider-result}}"
          - name: BENEFIT_DATA
            value: "{{inputs.parameters.benefit-result}}"
```

## Cost

| Component | Monthly Cost |
|-----------|--------------|
| Prometheus (50GB) | ~$5 |
| Grafana (10GB) | ~$3 |
| **Total** | **~$8/month** |

## Recommended Implementation Order

1. ✅ **Deploy Prometheus + Grafana** (30 min) ← **START HERE**
2. Complete Benefit Plan Service (1-2 hours)
3. Build Provider Directory Service (2-3 hours)
4. Deploy PostgreSQL + Reference Data (2-3 hours)
5. Complete Eligibility Service containerization (1 hour)
6. **Build Claims Adjudication Workflow** (2-3 hours) ← **BIG WIN**
7. Add custom metrics to all services (ongoing)

**Let's deploy monitoring now to get visibility!** 🎯
