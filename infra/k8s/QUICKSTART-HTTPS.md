# Quick Start: HTTPS Ingress Setup

## TL;DR - Deploy HTTPS Ingress in 5 Minutes

### Prerequisites
- Kubernetes cluster with kubectl configured
- Helm 3 installed
- DNS records pointing to **4.149.83.133**

### Step 1: Install NGINX Ingress Controller (2 min)

```bash
helm repo add ingress-nginx https://kubernetes.github.io/ingress-nginx
helm repo update
helm install ingress-nginx ingress-nginx/ingress-nginx \
  --namespace ingress-nginx \
  --create-namespace \
  --set controller.service.loadBalancerIP=4.149.83.133
```

### Step 2: Install cert-manager (1 min)

```bash
kubectl apply -f https://github.com/cert-manager/cert-manager/releases/download/v1.13.3/cert-manager.yaml
```

Wait for cert-manager pods to be ready:
```bash
kubectl wait --for=condition=ready pod -l app=cert-manager -n cert-manager --timeout=120s
```

### Step 3: Configure DNS

Add these A records to your DNS provider:

| Domain                         | Type | Value         |
|--------------------------------|------|---------------|
| cloudhealthoffice.com          | A    | 4.149.83.133  |
| www.cloudhealthoffice.com      | A    | 4.149.83.133  |
| portal.cloudhealthoffice.com   | A    | 4.149.83.133  |

### Step 4: Deploy Cloud Health Office with HTTPS (2 min)

```bash
cd /home/runner/work/cloudhealthoffice/cloudhealthoffice

# Apply cert-manager issuer
kubectl apply -f k8s/cert-manager-issuer.yaml

# Deploy website and portal with updated services
kubectl apply -f site/k8s/site-deployment.yaml
kubectl apply -f portal/CloudHealthOffice.Portal/k8s/portal-deployment.yaml

# Deploy ingress resources
kubectl apply -f k8s/site-ingress.yaml
kubectl apply -f k8s/portal-ingress.yaml
```

### Step 5: Wait for Certificates (1-3 min)

```bash
# Watch certificate issuance
kubectl get certificate -n cloudhealthoffice -w

# Wait until both show READY=True:
# - site-tls-secret
# - portal-tls-secret
```

### Step 6: Test HTTPS

```bash
# Test website
curl -I https://cloudhealthoffice.com

# Test portal
curl -I https://portal.cloudhealthoffice.com
```

Open in browser:
- https://cloudhealthoffice.com (should show green padlock)
- https://portal.cloudhealthoffice.com (should show green padlock)

## Done! 🎉

Your Cloud Health Office website and portal are now secured with HTTPS.

---

## Troubleshooting

**Certificates stuck in "Issuing" state?**

Check DNS propagation:
```bash
dig +short cloudhealthoffice.com
# Should return: 4.149.83.133
```

If DNS not propagated, wait and retry. If propagated but still failing:
```bash
kubectl logs -n cert-manager -l app=cert-manager --tail=50
```

**502 Bad Gateway?**

Check backend pods are running:
```bash
kubectl get pods -n cloudhealthoffice -l app=site
kubectl get pods -n cloudhealthoffice -l app=portal
```

**For detailed troubleshooting, see:** [k8s/INGRESS-HTTPS-SETUP.md](./INGRESS-HTTPS-SETUP.md)

---

## What Changed?

1. **Services**: Changed from LoadBalancer → ClusterIP
   - `site` service: ClusterIP (port 80)
   - `portal` service: ClusterIP (port 80→8080)

2. **Ingress**: Added HTTPS ingress resources
   - `site-ingress`: Serves cloudhealthoffice.com + www
   - `portal-ingress`: Serves portal.cloudhealthoffice.com

3. **TLS**: Automatic certificate management
   - cert-manager + Let's Encrypt
   - Auto-renewal every 60 days

4. **Security**: Enhanced headers
   - HSTS (Force HTTPS)
   - X-Frame-Options
   - X-Content-Type-Options
   - X-XSS-Protection

## Architecture

```
Internet (HTTPS)
      ↓
DNS (4.149.83.133)
      ↓
NGINX Ingress Controller (LoadBalancer)
      ↓
Ingress Resources (TLS termination)
      ↓
Services (ClusterIP)
      ↓
Pods (site, portal)
```

## Next Steps

- Update your CI/CD pipelines to use the new ingress URLs
- Configure monitoring for certificate expiration
- Set up rate limiting if needed (see full guide)
- Consider IP whitelisting for production access

**Full Documentation:** [k8s/INGRESS-HTTPS-SETUP.md](./INGRESS-HTTPS-SETUP.md)
