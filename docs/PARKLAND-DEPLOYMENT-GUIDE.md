# Parkland Community Health Plan - Deployment Guide

## Overview

This guide provides deployment instructions for Parkland Community Health Plan's private integration environment based on Cloud Health Office architecture. This deployment is specifically tailored for Parkland Hospital System's Azure subscription and network infrastructure.

## Table of Contents

- [Architecture Overview](#architecture-overview)
- [Prerequisites](#prerequisites)
- [Network Configuration](#network-configuration)
- [Deployment Steps](#deployment-steps)
- [QNXT Integration](#qnxt-integration)
- [Member Interoperability API](#member-interoperability-api)
- [Operations Migration Plan](#operations-migration-plan)
- [Monitoring and Support](#monitoring-and-support)

## Architecture Overview

### Deployment Model

- **Type**: Private deployment (non-SaaS)
- **Platform**: Azure Kubernetes Service (AKS)
- **Network**: Spoke VNet connected to Parkland Hospital ExpressRoute
- **Backend**: Cognizant QNXT via existing VPN
- **Authentication**: Okta for member-facing services

### High-Level Architecture

```
┌─────────────────────────────────────────────────────────────────┐
│           Parkland Hospital System Network                       │
│                  (ExpressRoute Hub)                              │
└────────────────────────┬────────────────────────────────────────┘
                         │
                         │ ExpressRoute
                         │
┌────────────────────────▼────────────────────────────────────────┐
│              Parkland CHO Spoke VNet                             │
│                 (Azure Subscription)                             │
│                                                                  │
│  ┌──────────────────────────────────────────────────────────┐  │
│  │    Azure Kubernetes Service (AKS)                        │  │
│  │                                                           │  │
│  │  ┌─────────────────┐    ┌─────────────────┐            │  │
│  │  │  Member API     │    │  File Ingestion │            │  │
│  │  │  + Okta Auth    │    │  Service        │            │  │
│  │  └─────────────────┘    └─────────────────┘            │  │
│  │                                                           │  │
│  │  ┌─────────────────┐    ┌─────────────────┐            │  │
│  │  │  FHIR Gateway   │    │  Argo Workflows │            │  │
│  │  └─────────────────┘    └─────────────────┘            │  │
│  └──────────────────────────────────────────────────────────┘  │
│                                                                  │
│  ┌──────────────────────────────────────────────────────────┐  │
│  │    Supporting Services                                    │  │
│  │  • Azure Key Vault (Premium)                             │  │
│  │  • Azure Storage (Data Lake Gen2)                        │  │
│  │  • Apache Kafka (managed)                                │  │
│  │  • Prometheus + Grafana                                  │  │
│  └──────────────────────────────────────────────────────────┘  │
└────────────────────────┬────────────────────────────────────────┘
                         │
                         │ Site-to-Site VPN
                         │
┌────────────────────────▼────────────────────────────────────────┐
│              Cognizant Network                                   │
│                                                                  │
│  ┌─────────────────┐    ┌─────────────────┐                    │
│  │  QNXT Claims    │    │  QNXT Member    │                    │
│  └─────────────────┘    └─────────────────┘                    │
│                                                                  │
│  ┌─────────────────┐    ┌─────────────────┐                    │
│  │  BPass SFTP     │    │  QNXT Elig.     │                    │
│  └─────────────────┘    └─────────────────┘                    │
└─────────────────────────────────────────────────────────────────┘

                         │
                         │ Internet (HTTPS)
                         │
┌────────────────────────▼────────────────────────────────────────┐
│              Member Mobile/Web Apps                              │
│                 (Okta Authentication)                            │
└─────────────────────────────────────────────────────────────────┘
```

## Prerequisites

### Azure Resources

1. **Azure Subscription**: Parkland Hospital System subscription
2. **Resource Group**: `parkland-cho-rg` (or custom name)
3. **Region**: Central US (or preferred region near Parkland)
4. **Permissions**: 
   - Contributor role on subscription
   - User Access Administrator (for managed identities)

### Network Prerequisites

1. **ExpressRoute Circuit**: Existing Parkland Hospital ExpressRoute
2. **VPN Gateway**: Existing VPN connection to Cognizant
3. **DNS Configuration**: Internal DNS resolution for QNXT endpoints
4. **Network Security**: 
   - NSG rules for AKS
   - Firewall rules for QNXT connectivity
   - Private endpoints for Azure services

### Tools Required

- Azure CLI (az) version 2.50+
- kubectl version 1.28+
- Helm version 3.12+
- Docker (for local development)
- jq (for JSON processing)

### Authentication Setup

1. **Okta Tenant**: Parkland Okta instance
2. **Service Principal**: For Terraform/deployment automation
3. **Managed Identity**: For AKS workload identity
4. **VPN Credentials**: Access to Cognizant network

## Network Configuration

### VNet Setup

```bash
# Create spoke VNet
az network vnet create \
  --resource-group parkland-cho-rg \
  --name parkland-cho-spoke-vnet \
  --address-prefix 10.200.0.0/16 \
  --location centralus

# Create AKS subnet
az network vnet subnet create \
  --resource-group parkland-cho-rg \
  --vnet-name parkland-cho-spoke-vnet \
  --name aks-subnet \
  --address-prefix 10.200.0.0/20

# Create services subnet
az network vnet subnet create \
  --resource-group parkland-cho-rg \
  --vnet-name parkland-cho-spoke-vnet \
  --name services-subnet \
  --address-prefix 10.200.16.0/24
```

### ExpressRoute Peering

```bash
# Peer spoke VNet to hub VNet (containing ExpressRoute gateway)
az network vnet peering create \
  --resource-group parkland-cho-rg \
  --name spoke-to-hub \
  --vnet-name parkland-cho-spoke-vnet \
  --remote-vnet /subscriptions/{subscription-id}/resourceGroups/{hub-rg}/providers/Microsoft.Network/virtualNetworks/{hub-vnet} \
  --allow-vnet-access \
  --allow-forwarded-traffic \
  --allow-gateway-transit false \
  --use-remote-gateways true
```

### VPN Route Configuration

Add routes to Cognizant network (configure on VPN gateway):

```
# Example routes (adjust based on actual Cognizant network)
Destination: 172.16.0.0/16  # Cognizant QNXT network
Next Hop: VPN Gateway
```

### DNS Configuration

Configure conditional DNS forwarding for Cognizant domains:

```
cognizant.parkland.internal -> Cognizant DNS servers
```

## Deployment Steps

### Step 1: Create AKS Cluster

```bash
# Create AKS cluster with Azure CNI and network policy
az aks create \
  --resource-group parkland-cho-rg \
  --name parkland-cho-aks \
  --location centralus \
  --node-count 3 \
  --node-vm-size Standard_D4s_v3 \
  --network-plugin azure \
  --network-policy calico \
  --vnet-subnet-id /subscriptions/{subscription-id}/resourceGroups/parkland-cho-rg/providers/Microsoft.Network/virtualNetworks/parkland-cho-spoke-vnet/subnets/aks-subnet \
  --enable-managed-identity \
  --enable-addons monitoring \
  --generate-ssh-keys \
  --zones 1 2 3

# Get credentials
az aks get-credentials \
  --resource-group parkland-cho-rg \
  --name parkland-cho-aks
```

### Step 2: Install Core Infrastructure

```bash
# Add Helm repositories
helm repo add argo https://argoproj.github.io/argo-helm
helm repo add bitnami https://charts.bitnami.com/bitnami
helm repo add prometheus-community https://prometheus-community.github.io/helm-charts
helm repo update

# Create namespaces
kubectl create namespace parkland-cho-system
kubectl create namespace parkland-cho-services
kubectl create namespace parkland-cho-workflows

# Install Argo Workflows
helm install argo-workflows argo/argo-workflows \
  --namespace parkland-cho-workflows \
  --version 0.41.0 \
  --set server.enabled=true \
  --set controller.workflowNamespaces={parkland-cho-workflows}

# Install Apache Kafka
helm install kafka bitnami/kafka \
  --namespace parkland-cho-system \
  --set replicaCount=3 \
  --set persistence.enabled=true \
  --set persistence.size=100Gi

# Install Prometheus + Grafana
helm install monitoring prometheus-community/kube-prometheus-stack \
  --namespace parkland-cho-system \
  --set grafana.enabled=true \
  --set prometheus.prometheusSpec.retention=30d
```

### Step 3: Deploy Azure Services

```bash
# Deploy infrastructure using Bicep
az deployment group create \
  --resource-group parkland-cho-rg \
  --template-file infra/parkland-infrastructure.bicep \
  --parameters @config/pchp-integration-config.json
```

### Step 4: Configure Secrets

```bash
# Create Key Vault
az keyvault create \
  --name parkland-cho-kv \
  --resource-group parkland-cho-rg \
  --location centralus \
  --sku premium \
  --enable-soft-delete true \
  --enable-purge-protection true

# Store QNXT API key
az keyvault secret set \
  --vault-name parkland-cho-kv \
  --name qnxt-api-key \
  --value "{qnxt-api-key}"

# Store Okta credentials
az keyvault secret set \
  --vault-name parkland-cho-kv \
  --name okta-member-api-client-id \
  --value "{okta-client-id}"

az keyvault secret set \
  --vault-name parkland-cho-kv \
  --name okta-member-api-client-secret \
  --value "{okta-client-secret}"

# Store Cognizant SFTP key
az keyvault secret set \
  --vault-name parkland-cho-kv \
  --name cognizant-sftp-key \
  --file ~/.ssh/cognizant_sftp_rsa
```

### Step 5: Deploy Member Interoperability API

```bash
# Deploy using Helm chart
helm install member-api ./helm/member-interoperability-api \
  --namespace parkland-cho-services \
  --set config.oktaDomain=parkland.okta.com \
  --set config.fhirEndpoint=https://fhir.parklandhospital.com/api \
  --set config.qnxtBackend.enabled=true
```

### Step 6: Configure Ingress

```bash
# Install NGINX Ingress Controller
helm install ingress-nginx ingress-nginx/ingress-nginx \
  --namespace parkland-cho-system \
  --set controller.service.annotations."service\.beta\.kubernetes\.io/azure-load-balancer-internal"="true"

# Apply ingress rules
kubectl apply -f k8s/ingress/member-api-ingress.yaml
```

## QNXT Integration

### Configuration

The QNXT integration uses the existing VPN connection to Cognizant. Ensure the following endpoints are accessible:

- Claims API: `https://qnxt-claims.cognizant.parkland.internal/api/v1`
- Eligibility API: `https://qnxt-eligibility.cognizant.parkland.internal/api/v1`
- Member API: `https://qnxt-member.cognizant.parkland.internal/api/v1`
- Provider API: `https://qnxt-provider.cognizant.parkland.internal/api/v1`

### Testing Connectivity

```bash
# From within AKS cluster, test QNXT connectivity
kubectl run qnxt-test --rm -it --image=curlimages/curl -- sh

# Test claims endpoint
curl -H "X-API-Key: {api-key}" \
  https://qnxt-claims.cognizant.parkland.internal/api/v1/health

# Expected response: 200 OK
```

### API Authentication

QNXT uses API key authentication. The key is stored in Azure Key Vault and injected into pods via CSI driver.

```yaml
# Example pod configuration
apiVersion: v1
kind: Pod
metadata:
  name: qnxt-client
spec:
  volumes:
    - name: secrets-store
      csi:
        driver: secrets-store.csi.k8s.io
        readOnly: true
        volumeAttributes:
          secretProviderClass: "qnxt-secrets"
  containers:
    - name: app
      image: parkland/qnxt-client:latest
      volumeMounts:
        - name: secrets-store
          mountPath: "/mnt/secrets"
          readOnly: true
      env:
        - name: QNXT_API_KEY
          valueFrom:
            secretKeyRef:
              name: qnxt-api-key
              key: apikey
```

## Member Interoperability API

### Okta Configuration

1. **Create Okta Application**:
   - Application Type: Native App
   - Grant Types: Authorization Code, Refresh Token
   - Redirect URIs: `https://members.parklandhospital.com/callback`
   - Scopes: openid, profile, email, fhir_user

2. **Configure Authorization Server**:
   - Issuer: `https://parkland.okta.com/oauth2/default`
   - Access Token Lifetime: 1 hour
   - Refresh Token Lifetime: 90 days

3. **Create Custom Scopes**:
   - `fhir_user`: Access to FHIR resources
   - `patient/*.read`: Read patient data
   - `coverage/*.read`: Read coverage data
   - `eob/*.read`: Read explanation of benefits

### API Endpoints

The Member Interoperability API exposes the following endpoints:

```
# Authentication
POST /auth/login
POST /auth/register
POST /auth/refresh
POST /auth/logout

# Member Profile
GET /api/v1/member/profile
PUT /api/v1/member/profile

# Healthcare Records
GET /api/v1/member/records
GET /api/v1/member/records/{id}
GET /api/v1/member/records/download

# Coverage
GET /api/v1/member/coverage
GET /api/v1/member/coverage/{id}

# Claims
GET /api/v1/member/claims
GET /api/v1/member/claims/{id}

# Explanation of Benefits
GET /api/v1/member/eob
GET /api/v1/member/eob/{id}
```

### Example Usage

```bash
# 1. Authenticate with Okta
curl -X POST https://parkland.okta.com/oauth2/default/v1/token \
  -H "Content-Type: application/x-www-form-urlencoded" \
  -d "grant_type=authorization_code" \
  -d "code={authorization_code}" \
  -d "redirect_uri=https://members.parklandhospital.com/callback" \
  -d "client_id={client_id}" \
  -d "client_secret={client_secret}"

# 2. Access member records
curl -X GET https://api.parklandhospital.com/api/v1/member/records \
  -H "Authorization: Bearer {access_token}" \
  -H "Accept: application/fhir+json"

# 3. Download records in CCD format
curl -X GET "https://api.parklandhospital.com/api/v1/member/records/download?format=ccd" \
  -H "Authorization: Bearer {access_token}" \
  -H "Accept: application/xml" \
  --output my-health-records.xml
```

## Operations Migration Plan

### Phase 1: Member Interoperability API (Q1 2026)

**Status**: Initial deployment

**Services**:
- Member registration and authentication via Okta
- FHIR R4 gateway for healthcare records
- Mobile app backend API
- Record download in multiple formats

**Success Criteria**:
- 100% uptime for member-facing API
- < 500ms API response time (95th percentile)
- Support for 10,000 registered members
- HIPAA compliance validation

### Phase 2: File Ingestion (Q2 2026)

**Status**: Planned

**Services**:
- SFTP ingestion from Cognizant BPass
- X12 file validation and parsing
- Automated archive to Azure Storage
- Error notification and retry logic

**Migration Steps**:
1. Set up parallel SFTP ingestion (Cognizant continues primary)
2. Validate file processing accuracy (100% match)
3. Implement file transformation workflows
4. Enable dead-letter queue handling
5. Gradual cutover from Cognizant to Parkland CHO

### Phase 3: Claims Processing (Q3 2026)

**Status**: Planned

**Services**:
- 837 claims submission processing
- 835 remittance processing
- 276/277 claim status inquiry
- Integration with QNXT for adjudication

**Dependencies**:
- Successful Phase 2 completion
- QNXT API expansion
- Provider onboarding

### Phase 4: Prior Authorization (Q4 2026)

**Status**: Planned

**Services**:
- 278 prior authorization requests
- Da Vinci PAS API implementation
- CRD integration for documentation requirements
- Authorization tracking and SLA monitoring

**Compliance**:
- CMS-0057-F compliance
- 72-hour urgent auth response
- 7-day standard auth response

### Phase 5: Full Operations (2027+)

**Status**: Future

**Scope**: Complete replacement of Cognizant BPass operations

**Services**:
- All EDI transaction types
- Provider portal
- Payer-to-payer data exchange
- Appeals processing
- Enhanced claim status
- Analytics and reporting

## Monitoring and Support

### Application Insights

All services send telemetry to Azure Application Insights:

```
Workspace ID: parkland-cho-logs
Key Vault Secret: application-insights-connection-string
```

### Prometheus Metrics

Access Grafana dashboards:

```bash
# Port-forward Grafana
kubectl port-forward -n parkland-cho-system svc/monitoring-grafana 3000:80

# Access at http://localhost:3000
# Default credentials: admin / prom-operator
```

### Key Dashboards

1. **Kubernetes Cluster Health**: Overall AKS health metrics
2. **Member API Performance**: API latency, error rates, throughput
3. **QNXT Integration**: Backend connectivity, API response times
4. **File Ingestion Status**: Files processed, errors, backlog
5. **Security & Compliance**: Authentication failures, audit events

### Alerting

Alerts are configured to send to:
- Email: itsupport@parklandhospital.com
- Microsoft Teams: Integration team channel

### Alert Rules

- API error rate > 1%
- API P95 latency > 1 second
- QNXT connectivity failure
- Okta authentication error rate > 0.1%
- File ingestion backlog > 100 files
- Kubernetes pod crash loops
- Storage capacity > 80%

### Support Contacts

**Parkland IT Support**:
- Email: itsupport@parklandhospital.com
- Phone: +1-214-590-8000
- On-call: Integration team rotation

**Cognizant QNXT Support**:
- Email: parkland-support@cognizant.com
- Phone: Per existing support contract

**Okta Support**:
- Portal: https://parkland.okta.com/admin
- Email: support@okta.com (Enterprise support)

## Security Considerations

### Network Security

- All traffic within Azure uses private endpoints
- No public internet access to backend services
- ExpressRoute-only connectivity to on-premises
- VPN-only access to Cognizant network
- NSG rules restrict traffic between subnets

### Data Encryption

- At rest: Azure Storage encryption with managed keys
- In transit: TLS 1.2+ for all communications
- Database: Transparent Data Encryption (TDE)
- Secrets: Azure Key Vault with Premium SKU

### HIPAA Compliance

- BAA with Microsoft Azure in place
- PHI data classification and handling procedures
- Audit logging enabled (2555-day retention)
- Access controls via RBAC and managed identities
- Regular security assessments and penetration testing

### Incident Response

1. Detect: Automated alerting via Azure Monitor
2. Respond: On-call team paged via PagerDuty
3. Investigate: Access logs and telemetry
4. Remediate: Apply fixes and validate
5. Report: Document incident and lessons learned
6. Review: Quarterly security review meetings

## Troubleshooting

### Common Issues

#### QNXT Connectivity Failures

```bash
# Check VPN status
az network vpn-connection show \
  --resource-group parkland-network-rg \
  --name parkland-cognizant-vpn

# Verify DNS resolution
kubectl run dns-test --rm -it --image=busybox -- nslookup qnxt-claims.cognizant.parkland.internal

# Test API endpoint
kubectl run api-test --rm -it --image=curlimages/curl -- \
  curl -v https://qnxt-claims.cognizant.parkland.internal/api/v1/health
```

#### Okta Authentication Issues

```bash
# Check Okta service status
curl https://parkland.okta.com/api/v1/health

# Verify client credentials in Key Vault
az keyvault secret show \
  --vault-name parkland-cho-kv \
  --name okta-member-api-client-id

# Check pod logs
kubectl logs -n parkland-cho-services deployment/member-api -f
```

#### AKS Pod Failures

```bash
# Check pod status
kubectl get pods -n parkland-cho-services

# View pod logs
kubectl logs -n parkland-cho-services <pod-name>

# Describe pod for events
kubectl describe pod -n parkland-cho-services <pod-name>

# Check node status
kubectl get nodes
kubectl describe node <node-name>
```

## Next Steps

1. **Complete Phase 1 Deployment**: Focus on member interoperability API
2. **Member Onboarding**: Enable member registration and app downloads
3. **Monitor Performance**: Establish baseline metrics
4. **Plan Phase 2**: Design file ingestion workflows
5. **QNXT API Expansion**: Work with Cognizant to expose additional endpoints
6. **Security Audit**: Third-party HIPAA compliance validation
7. **Disaster Recovery**: Implement backup and recovery procedures

## Appendix

### Configuration Files

- Primary config: `/config/pchp-integration-config.json`
- Kubernetes manifests: `/k8s/parkland/`
- Helm values: `/helm/parkland/values.yaml`
- Bicep templates: `/infra/parkland-infrastructure.bicep`

### References

- Cloud Health Office Architecture: `/docs/ARCHITECTURE.md`
- Kubernetes Deployment Guide: `/docs/MULTI-CLOUD-DEPLOYMENT.md`
- FHIR Integration: `/docs/FHIR-INTEGRATION.md`
- CMS-0057-F Compliance: `/docs/CMS-0057-F-COMPLIANCE.md`
- Security Hardening: `/SECURITY-HARDENING.md`

---

**Document Version**: 1.0  
**Last Updated**: January 2026  
**Owner**: Parkland Hospital System IT Department  
**Classification**: Internal Use Only
