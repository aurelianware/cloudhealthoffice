# HTTPS Ingress Configuration Guide for Cloud Health Office

## Overview

This guide explains how to configure Kubernetes ingress with HTTPS/TLS for the Cloud Health Office website and portal using NGINX Ingress Controller and cert-manager with Let's Encrypt.

## Architecture

- **Website**: Static marketing site at `cloudhealthoffice.com` and `www.cloudhealthoffice.com`
- **Portal**: ASP.NET Core application at `portal.cloudhealthoffice.com`
- **Ingress Controller**: NGINX Ingress Controller
- **TLS Certificates**: Automated via cert-manager and Let's Encrypt
- **External IP**: 4.149.83.133 (configured on ingress controller LoadBalancer)

## Prerequisites

### 1. Install NGINX Ingress Controller

```bash
# Add NGINX Ingress Helm repository
helm repo add ingress-nginx https://kubernetes.github.io/ingress-nginx
helm repo update

# Install NGINX Ingress Controller with LoadBalancer service
helm install ingress-nginx ingress-nginx/ingress-nginx \
  --namespace ingress-nginx \
  --create-namespace \
  --set controller.service.loadBalancerIP=4.149.83.133 \
  --set controller.service.externalTrafficPolicy=Local \
  --set controller.publishService.enabled=true
```

**Verify installation:**

```bash
# Check that the ingress controller is running
kubectl get pods -n ingress-nginx

# Verify the LoadBalancer service has the correct external IP
kubectl get svc -n ingress-nginx
# Should show EXTERNAL-IP: 4.149.83.133
```

### 2. Install cert-manager

cert-manager automates TLS certificate management using Let's Encrypt.

```bash
# Install cert-manager using kubectl
kubectl apply -f https://github.com/cert-manager/cert-manager/releases/download/v1.13.3/cert-manager.yaml

# Verify cert-manager is running
kubectl get pods -n cert-manager
```

**Wait for all cert-manager pods to be Ready (1/1):**
- cert-manager
- cert-manager-cainjector
- cert-manager-webhook

### 3. Configure DNS Records

Point your domains to the ingress controller external IP: **4.149.83.133**

Add the following DNS A records:

| Domain                         | Type | Value         | TTL  |
|--------------------------------|------|---------------|------|
| cloudhealthoffice.com          | A    | 4.149.83.133  | 300  |
| www.cloudhealthoffice.com      | A    | 4.149.83.133  | 300  |
| portal.cloudhealthoffice.com   | A    | 4.149.83.133  | 300  |

**Verify DNS propagation:**

```bash
# Check that DNS is resolving correctly
nslookup cloudhealthoffice.com
nslookup www.cloudhealthoffice.com
nslookup portal.cloudhealthoffice.com

# Or use dig
dig +short cloudhealthoffice.com
dig +short www.cloudhealthoffice.com
dig +short portal.cloudhealthoffice.com
```

All should return: **4.149.83.133**

## Deployment Steps

### 1. Apply cert-manager ClusterIssuer

This creates the Let's Encrypt certificate issuer:

```bash
cd /home/runner/work/cloudhealthoffice/cloudhealthoffice

# Apply ClusterIssuer for production Let's Encrypt
kubectl apply -f k8s/cert-manager-issuer.yaml
```

**Verify:**

```bash
kubectl get clusterissuer
# Should show: letsencrypt-prod and letsencrypt-staging
```

### 2. Update Services (ClusterIP)

The site and portal services have been updated from LoadBalancer to ClusterIP since ingress will handle external traffic:

```bash
# Apply updated site deployment
kubectl apply -f site/k8s/site-deployment.yaml

# Apply updated portal deployment
kubectl apply -f portal/CloudHealthOffice.Portal/k8s/portal-deployment.yaml
```

**Verify services:**

```bash
kubectl get svc -n cloudhealthoffice
# site and portal should show TYPE: ClusterIP (not LoadBalancer)
```

### 3. Deploy Ingress Resources

Apply the ingress configurations for website and portal:

```bash
# Deploy site ingress
kubectl apply -f k8s/site-ingress.yaml

# Deploy portal ingress
kubectl apply -f k8s/portal-ingress.yaml
```

**Verify ingress:**

```bash
kubectl get ingress -n cloudhealthoffice
# Should show site-ingress and portal-ingress with your domain names
```

### 4. Monitor Certificate Issuance

cert-manager will automatically request TLS certificates from Let's Encrypt:

```bash
# Watch certificate requests
kubectl get certificate -n cloudhealthoffice
kubectl get certificaterequest -n cloudhealthoffice

# Check certificate details
kubectl describe certificate site-tls-secret -n cloudhealthoffice
kubectl describe certificate portal-tls-secret -n cloudhealthoffice

# View cert-manager logs
kubectl logs -n cert-manager -l app=cert-manager -f
```

**Certificate Status:**
- **Issuing**: Certificate request in progress
- **Ready**: Certificate successfully issued and stored in secret

This process typically takes 1-3 minutes.

### 5. Verify HTTPS Access

Once certificates are issued (status: Ready), test HTTPS access:

```bash
# Test website
curl -I https://cloudhealthoffice.com
curl -I https://www.cloudhealthoffice.com

# Test portal
curl -I https://portal.cloudhealthoffice.com
```

**Expected response:**
- HTTP/2 200 (or 301/302 for redirects)
- Headers showing `Strict-Transport-Security`, `X-Content-Type-Options`, etc.

**Browser test:**
1. Open https://cloudhealthoffice.com in a browser
2. Check for green padlock icon (secure connection)
3. View certificate details - should show Let's Encrypt Authority

## Configuration Files

### Site Ingress (k8s/site-ingress.yaml)

- **Hosts**: cloudhealthoffice.com, www.cloudhealthoffice.com
- **TLS Secret**: site-tls-secret
- **Backend Service**: site:80
- **Features**:
  - Force HTTPS redirect
  - Security headers (X-Frame-Options, HSTS, etc.)
  - Automatic Let's Encrypt certificate

### Portal Ingress (k8s/portal-ingress.yaml)

- **Host**: portal.cloudhealthoffice.com
- **TLS Secret**: portal-tls-secret
- **Backend Service**: portal:80
- **Features**:
  - Force HTTPS redirect
  - Extended timeouts (300s) for long operations
  - Security headers
  - Automatic Let's Encrypt certificate

### ClusterIssuer (k8s/cert-manager-issuer.yaml)

- **Production Issuer**: letsencrypt-prod
- **Staging Issuer**: letsencrypt-staging (for testing)
- **Challenge Type**: HTTP-01 (via NGINX ingress)
- **Email**: admin@cloudhealthoffice.com (update as needed)

## Troubleshooting

### Certificate Not Issuing

If certificates remain in "Issuing" state for more than 5 minutes:

```bash
# Check certificate request details
kubectl describe certificaterequest -n cloudhealthoffice

# Check cert-manager logs for errors
kubectl logs -n cert-manager -l app=cert-manager --tail=100

# Common issues:
# 1. DNS not propagating - verify with: dig +short <domain>
# 2. HTTP-01 challenge failing - ensure ingress is reachable on port 80
# 3. Rate limits - use staging issuer for testing
```

**Solution for DNS issues:**
- Wait for DNS propagation (can take up to 48 hours)
- Use `nslookup` or `dig` to verify DNS resolution

**Solution for challenge failures:**
```bash
# Temporarily use staging issuer
# Edit ingress annotations:
cert-manager.io/cluster-issuer: letsencrypt-staging

# After successful test, switch back to production
```

### HTTP (port 80) Not Redirecting to HTTPS

```bash
# Check ingress annotations
kubectl get ingress site-ingress -n cloudhealthoffice -o yaml

# Verify these annotations are present:
# nginx.ingress.kubernetes.io/ssl-redirect: "true"
# nginx.ingress.kubernetes.io/force-ssl-redirect: "true"
```

### 502 Bad Gateway

This indicates the backend service is unreachable:

```bash
# Check pod status
kubectl get pods -n cloudhealthoffice -l app=site
kubectl get pods -n cloudhealthoffice -l app=portal

# Check service endpoints
kubectl get endpoints -n cloudhealthoffice site
kubectl get endpoints -n cloudhealthoffice portal

# View pod logs
kubectl logs -n cloudhealthoffice -l app=site
kubectl logs -n cloudhealthoffice -l app=portal
```

### Certificate Renewal

Let's Encrypt certificates are valid for 90 days. cert-manager automatically renews certificates 30 days before expiration.

**Check renewal status:**

```bash
kubectl get certificate -n cloudhealthoffice
# Check NOT AFTER column for expiration date

# Force renewal (if needed)
kubectl delete secret site-tls-secret -n cloudhealthoffice
kubectl delete certificate site-tls-secret -n cloudhealthoffice
kubectl apply -f k8s/site-ingress.yaml
```

## Security Considerations

### HIPAA Compliance

The ingress configuration includes security headers required for HIPAA compliance:

- **Strict-Transport-Security**: Forces HTTPS for 1 year
- **X-Frame-Options**: Prevents clickjacking attacks
- **X-Content-Type-Options**: Prevents MIME sniffing
- **X-XSS-Protection**: Enables browser XSS protection

### Rate Limiting (Optional)

Add rate limiting to prevent abuse:

```yaml
# Add to ingress annotations:
nginx.ingress.kubernetes.io/limit-rps: "10"
nginx.ingress.kubernetes.io/limit-connections: "10"
```

### IP Whitelisting (Optional)

Restrict access to specific IPs:

```yaml
# Add to ingress annotations:
nginx.ingress.kubernetes.io/whitelist-source-range: "192.168.1.0/24,10.0.0.0/8"
```

## Maintenance

### Updating Email for Let's Encrypt

Edit `k8s/cert-manager-issuer.yaml` and update the email address:

```yaml
spec:
  acme:
    email: your-new-email@cloudhealthoffice.com
```

Then apply:

```bash
kubectl apply -f k8s/cert-manager-issuer.yaml
```

### Updating Domain Names

To add or change domains:

1. Add DNS A record pointing to 4.149.83.133
2. Update ingress YAML files with new hosts
3. Apply changes: `kubectl apply -f k8s/site-ingress.yaml`
4. cert-manager will automatically request certificates for new domains

### Monitoring

Set up monitoring for certificate expiration:

```bash
# Install cert-manager Prometheus integration
kubectl apply -f https://raw.githubusercontent.com/cert-manager/cert-manager/release-1.13/deploy/charts/cert-manager/templates/servicemonitor.yaml

# View cert-manager metrics
kubectl port-forward -n cert-manager svc/cert-manager 9402:9402
# Access: http://localhost:9402/metrics
```

## Additional Resources

- [NGINX Ingress Controller Documentation](https://kubernetes.github.io/ingress-nginx/)
- [cert-manager Documentation](https://cert-manager.io/docs/)
- [Let's Encrypt Documentation](https://letsencrypt.org/docs/)
- [Kubernetes Ingress Documentation](https://kubernetes.io/docs/concepts/services-networking/ingress/)

## Quick Reference

**Check ingress status:**
```bash
kubectl get ingress -n cloudhealthoffice
```

**Check certificates:**
```bash
kubectl get certificate -n cloudhealthoffice
```

**View ingress controller logs:**
```bash
kubectl logs -n ingress-nginx -l app.kubernetes.io/component=controller -f
```

**Test HTTPS:**
```bash
curl -I https://cloudhealthoffice.com
curl -I https://portal.cloudhealthoffice.com
```

**View TLS certificate details:**
```bash
echo | openssl s_client -servername cloudhealthoffice.com -connect cloudhealthoffice.com:443 2>/dev/null | openssl x509 -noout -dates
```
