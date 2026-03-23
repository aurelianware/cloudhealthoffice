# Multi-Cloud Deployment Guide

Cloud Health Office runs on **Argo Workflows on AKS** as its orchestration platform. The Kubernetes-native architecture supports deployment to any cloud provider:

- **Azure AKS** - Primary deployment target
- **AWS EKS** - Supported via standard Kubernetes manifests
- **Google GKE** - Supported via standard Kubernetes manifests
- **Self-managed Kubernetes** - On-prem or custom clusters

> **History:** CHO originally used Azure Logic Apps for orchestration. That runtime has been retired -- see [ADR-004](../adr/004-remove-logic-apps.md) for details.

## Architecture Overview

| Component | Technology |
|-----------|-----------|
| **Workflow Orchestration** | Argo Workflows on Kubernetes |
| **Event Triggers** | Argo Events |
| **Message Streaming** | Apache Kafka |
| **File Transfer** | Custom SFTP container |
| **X12 EDI Processing** | C# microservices |
| **Storage** | S3-compatible (MinIO/Azure Blob/AWS S3) |
| **Secret Management** | Kubernetes Secrets, HashiCorp Vault, or cloud KMS |

## Prerequisites

- Kubernetes cluster (AKS, EKS, GKE, or self-managed)
- Helm 3.x installed
- kubectl configured for your cluster
- Sufficient total cluster resources: 8+ vCPUs and 16+ GB RAM minimum across all nodes (recommended: 3 nodes with 4 vCPUs, 8 GB RAM each)

## Deployment Steps

### 1. Add Helm Repositories

```bash
# Add required Helm repositories
helm repo add argo https://argoproj.github.io/argo-helm
helm repo add bitnami https://charts.bitnami.com/bitnami
helm repo add hashicorp https://helm.releases.hashicorp.com
helm repo update
```

### 2. Create Namespace

```bash
kubectl create namespace cloudhealthoffice
```

### 3. Deploy with Helm

```bash
cd helm/cloudhealthoffice

# Install with default values (uses embedded Kafka and MinIO)
helm install cloudhealthoffice . \
  --namespace cloudhealthoffice \
  --values values.yaml

# Or install with external services
helm install cloudhealthoffice . \
  --namespace cloudhealthoffice \
  --set kafka.external.enabled=true \
  --set kafka.external.bootstrapServers="kafka.example.com:9092" \
  --set minio.enabled=false \
  --set storage.endpoint="s3.amazonaws.com"
```

### 4. Configure Secrets

```bash
# Create SFTP credentials secret
kubectl create secret generic clearinghouse-sftp-secret \
  --namespace cloudhealthoffice \
  --from-literal=username=your-sftp-user \
  --from-literal=password=your-sftp-password \
  --from-literal=privateKey="$(cat ~/.ssh/sftp_key)"

# Create backend API secret
kubectl create secret generic claims-backend-api-secret \
  --namespace cloudhealthoffice \
  --from-literal=token=your-api-token \
  --from-literal=baseUrl=https://backend.api.example.com
```

### 5. Deploy Argo Workflow Manifests

```bash
# Deploy workflow templates from infrastructure/argo-workflows/
kubectl apply -f infrastructure/argo-workflows/
```

### 6. Verify Deployment

```bash
# Check pod status
kubectl get pods -n cloudhealthoffice

# Check Argo Workflows
kubectl get workflows -n cloudhealthoffice

# Access Argo UI
kubectl port-forward svc/argo-server -n cloudhealthoffice 2746:2746
# Open: https://localhost:2746
```

## Cloud-Specific Configurations

### Azure Kubernetes Service (AKS)

```bash
# Create AKS cluster with recommended settings
az aks create \
  --resource-group cloudhealthoffice-rg \
  --name cloudhealthoffice-aks \
  --node-count 3 \
  --node-vm-size Standard_D4s_v3 \
  --enable-managed-identity \
  --network-plugin azure \
  --enable-addons monitoring

# Get credentials
az aks get-credentials \
  --resource-group cloudhealthoffice-rg \
  --name cloudhealthoffice-aks
```

### Amazon EKS

```bash
# Create EKS cluster (using eksctl)
eksctl create cluster \
  --name cloudhealthoffice-eks \
  --region us-east-1 \
  --nodegroup-name standard-workers \
  --node-type m5.large \
  --nodes 3 \
  --managed
```

### Google GKE

```bash
# Create GKE cluster
gcloud container clusters create cloudhealthoffice-gke \
  --zone us-central1-a \
  --num-nodes 3 \
  --machine-type e2-standard-4 \
  --enable-ip-alias
```

---

## Secrets Management Options

Cloud Health Office supports multiple secrets management approaches for flexibility:

### Option A: Cloud-Native Key Management

| Cloud | Service | Configuration |
|-------|---------|---------------|
| Azure | Azure Key Vault | `--set vault.type=azure-keyvault` |
| AWS | AWS Secrets Manager | `--set vault.type=aws-secrets-manager` |
| GCP | Google Secret Manager | `--set vault.type=gcp-secret-manager` |

### Option B: HashiCorp Vault (Recommended for Multi-Cloud)

For true cloud independence, use HashiCorp Vault as a unified secrets management layer.

#### Why HashiCorp Vault?

- **Cloud Agnostic**: Same secrets management across Azure, AWS, GCP
- **Open Source**: Community edition available (Enterprise for advanced features)
- **Dynamic Secrets**: Auto-rotating credentials for databases, cloud APIs
- **Encryption as a Service**: Transit engine for application-level encryption
- **Audit Logging**: Detailed access logs for HIPAA compliance
- **Kubernetes Integration**: Native authentication with Service Accounts

#### Deploy HashiCorp Vault in Kubernetes

```bash
# Add HashiCorp Helm repo
helm repo add hashicorp https://helm.releases.hashicorp.com
helm repo update

# Deploy Vault in dev mode (for testing)
helm install vault hashicorp/vault \
  --namespace cloudhealthoffice \
  --set "server.dev.enabled=true"

# Deploy Vault in HA mode (for production)
helm install vault hashicorp/vault \
  --namespace cloudhealthoffice \
  --values vault-values.yaml
```

**vault-values.yaml** (Production HA Configuration):
```yaml
# HashiCorp Vault Production Configuration for Cloud Health Office
global:
  enabled: true
  tlsDisable: false

server:
  enabled: true

  # HA Configuration
  ha:
    enabled: true
    replicas: 3
    raft:
      enabled: true
      setNodeId: true
      config: |
        ui = true
        listener "tcp" {
          tls_disable = 0
          address = "[::]:8200"
          cluster_address = "[::]:8201"
          tls_cert_file = "/vault/userconfig/vault-tls/tls.crt"
          tls_key_file = "/vault/userconfig/vault-tls/tls.key"
        }
        storage "raft" {
          path = "/vault/data"
        }
        service_registration "kubernetes" {}

  # Resource limits
  resources:
    requests:
      memory: 256Mi
      cpu: 250m
    limits:
      memory: 512Mi
      cpu: 500m

  # Persistent storage
  dataStorage:
    enabled: true
    size: 10Gi
    storageClass: null  # Use default storage class

  # Audit logging (HIPAA requirement)
  auditStorage:
    enabled: true
    size: 10Gi

# Vault Agent Injector for automatic secret injection
injector:
  enabled: true
  replicas: 2
  resources:
    requests:
      memory: 64Mi
      cpu: 50m
    limits:
      memory: 128Mi
      cpu: 100m

# Vault UI
ui:
  enabled: true
  serviceType: ClusterIP
```

#### Configure Vault for Cloud Health Office

```bash
# Initialize Vault (first time only)
kubectl exec -it vault-0 -n cloudhealthoffice -- vault operator init

# Store unseal keys and root token securely!
# Unseal Vault (required after restart)
kubectl exec -it vault-0 -n cloudhealthoffice -- vault operator unseal

# Enable Kubernetes authentication
kubectl exec -it vault-0 -n cloudhealthoffice -- vault auth enable kubernetes

# Configure Kubernetes auth
kubectl exec -it vault-0 -n cloudhealthoffice -- vault write auth/kubernetes/config \
  kubernetes_host="https://$KUBERNETES_PORT_443_TCP_ADDR:443"

# Enable KV secrets engine
kubectl exec -it vault-0 -n cloudhealthoffice -- vault secrets enable -path=cloudhealthoffice kv-v2

# Store secrets
kubectl exec -it vault-0 -n cloudhealthoffice -- vault kv put cloudhealthoffice/sftp \
  username=sftp-user \
  password=sftp-password

kubectl exec -it vault-0 -n cloudhealthoffice -- vault kv put cloudhealthoffice/backend \
  apiToken=your-api-token \
  baseUrl=https://backend.api.example.com

kubectl exec -it vault-0 -n cloudhealthoffice -- vault kv put cloudhealthoffice/kafka \
  saslUsername=kafka-user \
  saslPassword=kafka-password

# Create policy for Cloud Health Office workloads
kubectl exec -it vault-0 -n cloudhealthoffice -- vault policy write cloudhealthoffice - <<EOF
path "cloudhealthoffice/data/*" {
  capabilities = ["read", "list"]
}
EOF

# Create role for Kubernetes service account
kubectl exec -it vault-0 -n cloudhealthoffice -- vault write auth/kubernetes/role/cloudhealthoffice \
  bound_service_account_names=argo-workflow-sa \
  bound_service_account_namespaces=cloudhealthoffice \
  policies=cloudhealthoffice \
  ttl=24h
```

#### Update Helm Values for Vault Integration

```yaml
# In helm/cloudhealthoffice/values.yaml
vault:
  enabled: true
  type: hashicorp  # Options: hashicorp, azure-keyvault, aws-secrets-manager
  address: "https://vault.cloudhealthoffice.svc:8200"  # Use HTTPS for PHI compliance
  role: "cloudhealthoffice"
  secretPath: "cloudhealthoffice/data"
  # For Vault Agent Injector annotations
  annotations:
    vault.hashicorp.com/agent-inject: "true"
    vault.hashicorp.com/role: "cloudhealthoffice"
    vault.hashicorp.com/agent-inject-secret-sftp: "cloudhealthoffice/data/sftp"
    vault.hashicorp.com/agent-inject-secret-backend: "cloudhealthoffice/data/backend"
```

#### Using Vault Secrets in Workflows

Secrets are automatically injected into pods via Vault Agent:

```yaml
# Example workflow pod annotation
apiVersion: argoproj.io/v1alpha1
kind: Workflow
metadata:
  name: x12-275-ingest
spec:
  templates:
    - name: sftp-fetch
      container:
        image: cloudhealthoffice/sftp-fetcher:latest
        volumeMounts:
          - name: vault-secrets
            mountPath: /vault/secrets
            readOnly: true
      # Vault Agent automatically injects secrets to /vault/secrets/
```

---

## Comparison: Azure Key Vault vs HashiCorp Vault

| Feature | Azure Key Vault | HashiCorp Vault |
|---------|----------------|-----------------|
| **Deployment** | Managed service | Self-hosted or HCP |
| **Multi-Cloud** | Azure only | Any cloud / on-prem |
| **Open Source** | No | Yes (Community Edition) |
| **HSM Support** | Premium SKU | Enterprise Edition |
| **Dynamic Secrets** | Limited | Full support |
| **Secret Rotation** | Manual / limited | Automated |
| **Kubernetes Native** | Via CSI driver | Agent Injector |
| **Cost** | Pay-per-operation | Free (self-hosted) |
| **Compliance** | SOC, HIPAA, FedRAMP | SOC, HIPAA (Enterprise) |
| **Learning Curve** | Low | Medium |

### Recommendation

- **Azure-only deployments**: Use Azure Key Vault (simpler, integrated)
- **Multi-cloud / cloud independence**: Use HashiCorp Vault
- **Enterprise with compliance needs**: Consider HashiCorp Vault Enterprise for advanced audit and HSM features

---

## Security Considerations

### HIPAA Compliance Checklist

| Control | Implementation |
|---------|---------------|
| Encryption at Rest | etcd encryption, storage-level encryption |
| Encryption in Transit | mTLS with Istio / TLS 1.2+ |
| Access Control | Kubernetes RBAC + Vault policies |
| Audit Logging | Vault audit + Prometheus + Argo workflow logs |
| Network Isolation | Kubernetes Network Policies |
| Secret Management | HashiCorp Vault or cloud KMS |
| Data Retention | Configurable retention policies |

### Production Hardening

For production deployments:

```bash
# Enable network policies
kubectl apply -f k8s/network-policies/

# Enable pod security policies
kubectl apply -f k8s/pod-security/

# Configure audit logging
kubectl apply -f k8s/audit-policies/
```

---

## Support and Resources

- **Argo Workflows Docs**: https://argo-workflows.readthedocs.io/
- **Kubernetes Issues**: [k8s/TROUBLESHOOTING.md](../k8s/TROUBLESHOOTING.md)
- **HashiCorp Vault Docs**: https://developer.hashicorp.com/vault/docs
- **Community Support**: [GitHub Discussions](https://github.com/aurelianware/cloudhealthoffice/discussions)

---

**Cloud Health Office** -- Deploy Anywhere, Process Everywhere

*Open Source | Multi-Cloud | Production-Grade | HIPAA-Compliant*
