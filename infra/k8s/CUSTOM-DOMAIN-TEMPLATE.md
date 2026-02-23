# Custom Domain Configuration Template

Use this template to configure your own custom domain instead of cloudhealthoffice.com.

## Step 1: Update Domain Names

### Option A: Using sed (Quick Replace)

```bash
# Set your custom domain
export CUSTOM_DOMAIN="yourdomain.com"
export CUSTOM_IP="4.149.83.133"  # Your LoadBalancer IP

# Update site ingress
sed -i "s/cloudhealthoffice\.com/${CUSTOM_DOMAIN}/g" k8s/site-ingress.yaml

# Update portal ingress (portal subdomain)
sed -i "s/portal\.cloudhealthoffice\.com/portal.${CUSTOM_DOMAIN}/g" k8s/portal-ingress.yaml

# Update cert-manager email
sed -i "s/admin@cloudhealthoffice\.com/admin@${CUSTOM_DOMAIN}/g" k8s/cert-manager-issuer.yaml
```

### Option B: Manual Edit

Edit the following files and replace domain names:

**k8s/site-ingress.yaml:**
```yaml
spec:
  tls:
  - hosts:
    - yourdomain.com              # <-- Change this
    - www.yourdomain.com          # <-- Change this
    secretName: site-tls-secret
  rules:
  - host: yourdomain.com          # <-- Change this
    # ...
  - host: www.yourdomain.com      # <-- Change this
    # ...
```

**k8s/portal-ingress.yaml:**
```yaml
spec:
  tls:
  - hosts:
    - portal.yourdomain.com       # <-- Change this
    secretName: portal-tls-secret
  rules:
  - host: portal.yourdomain.com   # <-- Change this
    # ...
```

**k8s/cert-manager-issuer.yaml:**
```yaml
spec:
  acme:
    email: admin@yourdomain.com   # <-- Change this
```

## Step 2: Configure DNS

Add A records to your DNS provider:

| Record Type | Name                  | Value           | TTL  |
|-------------|-----------------------|-----------------|------|
| A           | @                     | 4.149.83.133    | 300  |
| A           | www                   | 4.149.83.133    | 300  |
| A           | portal                | 4.149.83.133    | 300  |

**Example for common DNS providers:**

### Cloudflare
1. Login to Cloudflare
2. Select your domain
3. Go to DNS → Records
4. Add three A records with IP: 4.149.83.133
   - Type: A, Name: @, IPv4: 4.149.83.133
   - Type: A, Name: www, IPv4: 4.149.83.133
   - Type: A, Name: portal, IPv4: 4.149.83.133
5. Set Proxy status: DNS only (orange cloud OFF)

### AWS Route 53
```bash
aws route53 change-resource-record-sets \
  --hosted-zone-id Z1234567890ABC \
  --change-batch '{
    "Changes": [
      {
        "Action": "UPSERT",
        "ResourceRecordSet": {
          "Name": "yourdomain.com",
          "Type": "A",
          "TTL": 300,
          "ResourceRecords": [{"Value": "4.149.83.133"}]
        }
      },
      {
        "Action": "UPSERT",
        "ResourceRecordSet": {
          "Name": "www.yourdomain.com",
          "Type": "A",
          "TTL": 300,
          "ResourceRecords": [{"Value": "4.149.83.133"}]
        }
      },
      {
        "Action": "UPSERT",
        "ResourceRecordSet": {
          "Name": "portal.yourdomain.com",
          "Type": "A",
          "TTL": 300,
          "ResourceRecords": [{"Value": "4.149.83.133"}]
        }
      }
    ]
  }'
```

### GCP Cloud DNS
```bash
gcloud dns record-sets transaction start --zone=your-zone
gcloud dns record-sets transaction add 4.149.83.133 \
  --name=yourdomain.com. --ttl=300 --type=A --zone=your-zone
gcloud dns record-sets transaction add 4.149.83.133 \
  --name=www.yourdomain.com. --ttl=300 --type=A --zone=your-zone
gcloud dns record-sets transaction add 4.149.83.133 \
  --name=portal.yourdomain.com. --ttl=300 --type=A --zone=your-zone
gcloud dns record-sets transaction execute --zone=your-zone
```

### Azure DNS
```bash
az network dns record-set a add-record \
  --resource-group myResourceGroup \
  --zone-name yourdomain.com \
  --record-set-name @ \
  --ipv4-address 4.149.83.133

az network dns record-set a add-record \
  --resource-group myResourceGroup \
  --zone-name yourdomain.com \
  --record-set-name www \
  --ipv4-address 4.149.83.133

az network dns record-set a add-record \
  --resource-group myResourceGroup \
  --zone-name yourdomain.com \
  --record-set-name portal \
  --ipv4-address 4.149.83.133
```

## Step 3: Verify DNS Propagation

```bash
# Wait for DNS to propagate (can take 5 minutes to 48 hours)
dig +short yourdomain.com
dig +short www.yourdomain.com
dig +short portal.yourdomain.com

# All should return: 4.149.83.133
```

Or use online tools:
- https://www.whatsmydns.net/
- https://dnschecker.org/

## Step 4: Test with Staging Certificates (Optional)

Before using production Let's Encrypt, test with staging to avoid rate limits:

**Temporarily change ingress annotations:**

```yaml
# In k8s/site-ingress.yaml and k8s/portal-ingress.yaml
annotations:
  cert-manager.io/cluster-issuer: letsencrypt-staging  # <-- Change from prod
```

Deploy and verify:
```bash
kubectl apply -f k8s/site-ingress.yaml
kubectl apply -f k8s/portal-ingress.yaml

# Wait for certificates
kubectl get certificate -n cloudhealthoffice -w

# Test (will show certificate warning - expected for staging)
curl -k -I https://yourdomain.com
```

**Once successful, switch back to production:**

```yaml
annotations:
  cert-manager.io/cluster-issuer: letsencrypt-prod  # <-- Back to prod
```

```bash
# Delete staging certificates
kubectl delete certificate -n cloudhealthoffice --all
kubectl delete secret -n cloudhealthoffice site-tls-secret portal-tls-secret

# Reapply with production issuer
kubectl apply -f k8s/site-ingress.yaml
kubectl apply -f k8s/portal-ingress.yaml
```

## Step 5: Deploy

```bash
cd /path/to/cloudhealthoffice

# Apply cert-manager issuer (with your custom email)
kubectl apply -f k8s/cert-manager-issuer.yaml

# Deploy site and portal
kubectl apply -f site/k8s/site-deployment.yaml
kubectl apply -f portal/CloudHealthOffice.Portal/k8s/portal-deployment.yaml

# Deploy ingress resources (with your custom domains)
kubectl apply -f k8s/site-ingress.yaml
kubectl apply -f k8s/portal-ingress.yaml

# Monitor certificate issuance
kubectl get certificate -n cloudhealthoffice -w
```

## Step 6: Verify HTTPS

```bash
# Test your custom domain
curl -I https://yourdomain.com
curl -I https://www.yourdomain.com
curl -I https://portal.yourdomain.com

# Check certificate details
echo | openssl s_client -servername yourdomain.com -connect yourdomain.com:443 2>/dev/null | openssl x509 -noout -issuer -subject -dates
```

Expected output:
- Issuer: Let's Encrypt Authority X3
- Subject: CN = yourdomain.com
- Valid from/to dates

## Troubleshooting Custom Domains

### DNS Not Resolving

**Problem:** `dig yourdomain.com` returns no results or wrong IP

**Solution:**
1. Verify DNS records in your provider's control panel
2. Wait longer (DNS can take up to 48 hours)
3. Clear local DNS cache:
   ```bash
   # Linux
   sudo systemd-resolve --flush-caches
   
   # macOS
   sudo dscacheutil -flushcache
   
   # Windows
   ipconfig /flushdns
   ```

### Certificate Not Issuing

**Problem:** Certificate stuck in "Issuing" state

**Check cert-manager logs:**
```bash
kubectl logs -n cert-manager -l app=cert-manager --tail=100
```

**Common errors:**

1. **DNS not resolving:** Wait for DNS propagation
2. **HTTP-01 challenge failed:** 
   - Ensure ingress controller is running
   - Check port 80 is accessible
   - Verify DNS points to correct IP

3. **Rate limited by Let's Encrypt:**
   - Use staging issuer for testing
   - Wait 1 hour for rate limit reset

### Wrong IP Address on LoadBalancer

**Problem:** Ingress controller has different IP than 4.149.83.133

**Solution:**
```bash
# Check current external IP
kubectl get svc -n ingress-nginx ingress-nginx-controller

# If different, update the ingress installation
helm upgrade ingress-nginx ingress-nginx/ingress-nginx \
  --namespace ingress-nginx \
  --set controller.service.loadBalancerIP=4.149.83.133
```

### Multiple Domains/Subdomains

To add more subdomains (e.g., api.yourdomain.com):

1. Add DNS A record: api → 4.149.83.133
2. Update ingress to include new host:

```yaml
spec:
  tls:
  - hosts:
    - yourdomain.com
    - www.yourdomain.com
    - api.yourdomain.com        # <-- Add here
    - portal.yourdomain.com
    secretName: site-tls-secret
  rules:
  - host: api.yourdomain.com    # <-- Add new rule
    http:
      paths:
      - path: /
        pathType: Prefix
        backend:
          service:
            name: your-api-service
            port:
              number: 8080
```

## Environment-Specific Domains

For dev/staging/production environments:

**Development:**
- dev.yourdomain.com → site
- dev-portal.yourdomain.com → portal

**Staging:**
- staging.yourdomain.com → site
- staging-portal.yourdomain.com → portal

**Production:**
- yourdomain.com, www.yourdomain.com → site
- portal.yourdomain.com → portal

Create separate ingress files for each environment and namespace them appropriately.

## Reference

- Original template: `k8s/site-ingress.yaml`, `k8s/portal-ingress.yaml`
- Full guide: `k8s/INGRESS-HTTPS-SETUP.md`
- Quick start: `k8s/QUICKSTART-HTTPS.md`
