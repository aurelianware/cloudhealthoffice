# SFTP DNS and IP Whitelisting Setup

## Overview

Customer-owned SFTP configuration with:
- **Custom DNS name** (e.g., `sftp.cloudhealthoffice.com`)
- **IP whitelisting** for clearinghouses and AKS/Argo Workflows
- **SSL/TLS certificate** (optional, for FTPS)

---

## Step 1: DNS Configuration

### Option A: Azure DNS Zone (Recommended)

```bash
# Create DNS zone
az network dns zone create \
  --resource-group rg-hipaa-aks \
  --name cloudhealthoffice.com

# Get the LoadBalancer IP
export SFTP_IP=$(kubectl get svc sftp-service -n cho-sftp -o jsonpath='{.status.loadBalancer.ingress[0].ip}')
echo "SFTP IP: $SFTP_IP"

# Create A record
az network dns record-set a add-record \
  --resource-group rg-hipaa-aks \
  --zone-name cloudhealthoffice.com \
  --record-set-name sftp \
  --ipv4-address $SFTP_IP

# Verify DNS record
az network dns record-set a show \
  --resource-group rg-hipaa-aks \
  --zone-name cloudhealthoffice.com \
  --name sftp

# Get nameservers to configure at registrar
az network dns zone show \
  --resource-group rg-hipaa-aks \
  --name cloudhealthoffice.com \
  --query nameServers -o table
```

**Update your domain registrar:**
- Point `cloudhealthoffice.com` to Azure DNS nameservers
- Wait 5-60 minutes for DNS propagation

### Option B: Existing DNS Provider

Add an **A record** in your DNS provider:
```
Type:  A
Name:  sftp
Value: 52.168.45.123  (your LoadBalancer IP)
TTL:   300
```

Result: `sftp.cloudhealthoffice.com` → `52.168.45.123`

### Verify DNS Resolution

```bash
# Check DNS propagation
dig sftp.cloudhealthoffice.com +short
nslookup sftp.cloudhealthoffice.com

# Test SFTP connection with DNS name
sftp logicapp@sftp.cloudhealthoffice.com
```

---

## Step 2: IP Whitelisting

### Gather Required IP Addresses

#### 1. AKS Cluster Outbound IPs

```bash
# Get AKS cluster resource
AKS_CLUSTER_NAME="cho-prod-aks"
RESOURCE_GROUP="rg-hipaa-aks"

# Get outbound IP addresses (AKS load balancer)
az aks show \
  --name $AKS_CLUSTER_NAME \
  --resource-group $RESOURCE_GROUP \
  --query "networkProfile.loadBalancerProfile.effectiveOutboundIPs[].id" -o tsv

# Or get the public IP directly
az network public-ip list --resource-group MC_${RESOURCE_GROUP}_${AKS_CLUSTER_NAME}_westus2 \
  --query "[].ipAddress" -o tsv
```

#### 2. Clearinghouse IP Addresses

**Availity:**
```
# Production IPs (confirm with Availity support)
50.207.21.0/24
50.207.22.0/24
```

**Change Healthcare:**
```
# Production IPs (confirm with Change Healthcare)
52.20.0.0/16
52.86.0.0/16
```

**Optum:**
```
# Contact Optum for current IP ranges
# Typically provided in onboarding docs
```

**Other Clearinghouses:**
- Contact your specific clearinghouse for their IP ranges
- Request both production and UAT/test IPs

#### 3. Your Admin IPs (optional, for testing)

```bash
# Get your current public IP
curl -4 ifconfig.me
# Example: 203.0.113.45
```

### Update Kubernetes Service with IP Whitelist

Edit the SFTP service to add source IP restrictions:

```bash
kubectl edit svc sftp-service -n cho-sftp
```

**Add this to the spec:**

```yaml
apiVersion: v1
kind: Service
metadata:
  name: sftp-service
  namespace: cho-sftp
spec:
  type: LoadBalancer
  # ADD THESE LINES:
  loadBalancerSourceRanges:
    # AKS cluster outbound IPs
    - 13.88.3.11/32
    - 13.88.3.12/32
    - 13.88.3.13/32
    - 13.88.3.14/32
    # Availity
    - 50.207.21.0/24
    - 50.207.22.0/24
    # Change Healthcare
    - 52.20.0.0/16
    - 52.86.0.0/16
    # Your admin IP (for testing)
    - 203.0.113.45/32
  ports:
    - port: 22
      targetPort: 22
      protocol: TCP
  selector:
    app: sftp-server
```

**Or apply directly:**

```bash
kubectl patch svc sftp-service -n cho-sftp -p '{
  "spec": {
    "loadBalancerSourceRanges": [
      "13.88.3.11/32",
      "13.88.3.12/32",
      "13.88.3.13/32",
      "13.88.3.14/32",
      "50.207.21.0/24",
      "50.207.22.0/24",
      "52.20.0.0/16",
      "52.86.0.0/16",
      "203.0.113.45/32"
    ]
  }
}'
```

### Verify Whitelisting

```bash
# From allowed IP - should work
sftp logicapp@sftp.cloudhealthoffice.com

# From non-whitelisted IP - should timeout
# Connection should be blocked at LoadBalancer level
```

---

## Step 3: Update Infrastructure Configuration

### Update Bicep Parameters

Edit `infra/main.parameters.json`:

```json
{
  "sftpHost": {
    "value": "sftp.cloudhealthoffice.com"
  }
}
```

**Or use environment-specific parameters:**

```json
{
  "sftpHost": {
    "value": "sftp-prod.cloudhealthoffice.com"
  }
}
```

For UAT:
```json
{
  "sftpHost": {
    "value": "sftp-uat.cloudhealthoffice.com"
  }
}
```

### Update API Connection

```bash
./scripts/configure-sftp-connection.sh
# When prompted, use: sftp.cloudhealthoffice.com
```

**Or manually:**

```bash
az deployment group create \
  --resource-group rg-hipaa-aks \
  --template-file infra/main.bicep \
  --parameters \
    sftpHost="sftp.cloudhealthoffice.com" \
    sftpUsername="logicapp" \
    sftpPassword="@Microsoft.KeyVault(SecretUri=https://cho-secrets.vault.azure.net/secrets/sftp-logicapp-password/)"
```

---

## Step 4: Update SFTP Deployment (Permanent Whitelist)

Update `k8s/sftp-server-deployment.yaml`:

```yaml
---
apiVersion: v1
kind: Service
metadata:
  name: sftp-service
  namespace: cho-sftp
  annotations:
    service.beta.kubernetes.io/azure-load-balancer-resource-group: "rg-hipaa-aks"
spec:
  type: LoadBalancer
  loadBalancerSourceRanges:
    # AKS cluster outbound IPs (update with actual values)
    - 13.88.3.11/32
    - 13.88.3.12/32
    - 13.88.3.13/32
    - 13.88.3.14/32
    # Availity clearinghouse
    - 50.207.21.0/24
    - 50.207.22.0/24
    # Change Healthcare
    - 52.20.0.0/16
    - 52.86.0.0/16
    # Optum (get actual ranges from Optum)
    # - X.X.X.X/X
    # Admin access (optional - remove for production)
    - 203.0.113.45/32
  ports:
    - port: 22
      targetPort: 22
      protocol: TCP
      name: sftp
  selector:
    app: sftp-server
```

**Apply changes:**

```bash
kubectl apply -f k8s/sftp-server-deployment.yaml
```

---

## Production Checklist

### DNS
- [ ] DNS A record created (`sftp.cloudhealthoffice.com`)
- [ ] DNS propagation verified (`dig`/`nslookup`)
- [ ] SFTP connection works with DNS name
- [ ] Updated all documentation with DNS name

### IP Whitelisting
- [ ] AKS cluster outbound IPs identified
- [ ] Clearinghouse IP ranges documented
- [ ] `loadBalancerSourceRanges` configured in Kubernetes
- [ ] Tested connection from allowed IP
- [ ] Verified blocking from non-whitelisted IP
- [ ] Removed admin IP from production whitelist

### Infrastructure
- [ ] Bicep parameters updated with DNS name
- [ ] API connection reconfigured with DNS name
- [ ] Argo Workflows tested end-to-end
- [ ] Monitoring alerts configured for connection failures

### Documentation
- [ ] Clearinghouse IP contacts documented
- [ ] DNS change process documented
- [ ] Runbook for adding new IPs
- [ ] Incident response plan if IPs change

---

## Monitoring & Alerts

### Test Connections Regularly

```bash
#!/bin/bash
# test-sftp-access.sh

# Test from AKS cluster region
az aks show \
  --name cho-prod-aks \
  --resource-group rg-hipaa-aks \
  --query "networkProfile.loadBalancerProfile.effectiveOutboundIPs[].id" -o tsv | while read ip_id; do
  echo "AKS outbound IP resource: $ip_id"
  # Note: Test connectivity from within an Argo Workflow pod
done

# Test DNS resolution
echo "Testing DNS resolution:"
dig sftp.cloudhealthoffice.com +short

# Test connection (from whitelisted IP)
echo "Testing SFTP connection:"
timeout 5 sftp -v logicapp@sftp.cloudhealthoffice.com <<EOF
bye
EOF

if [ $? -eq 0 ]; then
  echo "✅ SFTP connection successful"
else
  echo "❌ SFTP connection failed"
fi
```

### Azure Monitor Alerts

```bash
# Create alert for connection failures
az monitor metrics alert create \
  --name sftp-connection-failures \
  --resource-group rg-hipaa-aks \
  --scopes "/subscriptions/.../resourceGroups/rg-hipaa-aks/providers/Microsoft.Web/connections/cho-sftp" \
  --condition "count > 5" \
  --window-size 5m \
  --evaluation-frequency 1m \
  --action email user@example.com
```

---

## Troubleshooting

### DNS Not Resolving

```bash
# Check DNS record
az network dns record-set a show \
  --resource-group rg-hipaa-aks \
  --zone-name cloudhealthoffice.com \
  --name sftp

# Check TTL (wait for expiration)
dig sftp.cloudhealthoffice.com +noall +answer

# Force DNS refresh (macOS)
sudo dscacheutil -flushcache
sudo killall -HUP mDNSResponder

# Force DNS refresh (Linux)
sudo systemd-resolve --flush-caches
```

### Connection Refused Despite Whitelisting

**Check LoadBalancer configuration:**
```bash
kubectl describe svc sftp-service -n cho-sftp | grep -A 10 "LoadBalancer"
```

**Verify source IP ranges applied:**
```bash
kubectl get svc sftp-service -n cho-sftp -o yaml | grep -A 20 loadBalancerSourceRanges
```

**Check Azure NSG rules (if using internal LB):**
```bash
az network nsg rule list \
  --resource-group rg-hipaa-aks \
  --nsg-name your-nsg-name \
  --output table
```

### Argo Workflows Can't Connect After DNS Change

1. **Test API connection:**
   ```bash
   az resource invoke-action \
     --resource-group rg-hipaa-aks \
     --resource-type Microsoft.Web/connections \
     --name cho-sftp \
     --action testConnection \
     --api-version 2016-06-01
   ```

2. **Verify AKS pods can resolve DNS:**
   - AKS uses CoreDNS and Azure DNS by default
   - Check if custom DNS is configured in VNet

3. **Re-authorize connection:**
   - Azure Portal → API Connections → cho-sftp → Edit API connection
   - Update hostname to DNS name
   - Test connection

### Clearinghouse IP Changed

**Update whitelist immediately:**
```bash
kubectl patch svc sftp-service -n cho-sftp -p '{
  "spec": {
    "loadBalancerSourceRanges": ["NEW_IP/32", ...existing IPs...]
  }
}'

# Apply changes immediately (no pod restart needed)
```

---

## Advanced Configuration

### Multiple Environments

Create environment-specific DNS records:

```bash
# Production
sftp-prod.cloudhealthoffice.com → 52.168.45.123

# UAT
sftp-uat.cloudhealthoffice.com → 52.168.45.124

# Development
sftp-dev.cloudhealthoffice.com → 52.168.45.125
```

**Deploy separate SFTP servers:**
```bash
# Production namespace
kubectl create namespace cho-sftp-prod
kubectl apply -f k8s/sftp-server-deployment.yaml -n cho-sftp-prod

# UAT namespace
kubectl create namespace cho-sftp-uat
kubectl apply -f k8s/sftp-server-deployment.yaml -n cho-sftp-uat
```

### Geographic Redundancy

Deploy SFTP servers in multiple regions:

```bash
# West US
sftp-west.cloudhealthoffice.com → LoadBalancer in westus2

# East US
sftp-east.cloudhealthoffice.com → LoadBalancer in eastus2

# Use Azure Traffic Manager for automatic failover
```

---

## Cost Optimization

**Azure DNS Zone:** ~$0.50/month
**Public IP (LoadBalancer):** ~$3.65/month per IP
**No additional cost for IP whitelisting**

**Total estimated cost:** ~$4-5/month

---

## Related Documentation

- [SFTP Integration Guide](./SFTP-INTEGRATION-GUIDE.md)
- [SFTP Architecture](./SFTP-ARCHITECTURE.md)
- [SFTP Quick Start](./SFTP-QUICKSTART.md)
