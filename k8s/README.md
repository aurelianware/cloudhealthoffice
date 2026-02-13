# Kubernetes Deployment Configurations

This directory contains Kubernetes manifests for deploying Cloud Health Office components.

## HTTPS Ingress Setup (NEW)

### Quick Start
For a 5-minute setup guide with HTTPS enabled:
- **[QUICKSTART-HTTPS.md](./QUICKSTART-HTTPS.md)** - Fast deployment with cert-manager and Let's Encrypt

### Comprehensive Guide
For complete documentation with troubleshooting:
- **[INGRESS-HTTPS-SETUP.md](./INGRESS-HTTPS-SETUP.md)** - Full HTTPS ingress configuration guide

### Ingress Resources

#### Website Ingress
- **File**: `site-ingress.yaml`
- **Domains**: cloudhealthoffice.com, www.cloudhealthoffice.com
- **Backend**: site service (port 80)
- **TLS**: Automatic via Let's Encrypt (site-tls-secret)

#### Portal Ingress
- **File**: `portal-ingress.yaml`
- **Domain**: portal.cloudhealthoffice.com
- **Backend**: portal service (port 80→8080)
- **TLS**: Automatic via Let's Encrypt (portal-tls-secret)

#### TLS Certificate Management
- **File**: `cert-manager-issuer.yaml`
- **Issuers**: letsencrypt-prod, letsencrypt-staging
- **Protocol**: ACME with HTTP-01 challenge
- **Auto-renewal**: 30 days before expiration

## Directory Structure

```
k8s/
├── INGRESS-HTTPS-SETUP.md          # Complete HTTPS ingress guide
├── QUICKSTART-HTTPS.md             # 5-minute quick start
├── site-ingress.yaml               # Website HTTPS ingress
├── portal-ingress.yaml             # Portal HTTPS ingress
├── cert-manager-issuer.yaml        # Let's Encrypt issuers
├── namespaces.yaml                 # Namespace definitions
├── backend-api-integration.yaml    # Backend API configuration
├── coverage-service-deployment.yaml
├── member-service-deployment.yaml
├── mock-services-deployment.yaml
├── sftp-server-deployment.yaml
├── sponsor-service-deployment.yaml
├── x12-275-upload-job.yaml         # HIPAA X12 275 jobs
├── x12-277-download-job.yaml       # HIPAA X12 277 jobs
├── x12-278-upload-job.yaml         # HIPAA X12 278 jobs
├── x12-837-claims-jobs.yaml        # HIPAA X12 837 jobs
├── configmaps/                     # ConfigMap definitions
├── secrets/                        # Secret templates
└── rbac/                           # RBAC policies
```

## Namespaces

- **cho-portal**: Frontend applications
- **cloudhealthoffice**: Backend services, website, portal
- **cho-workflows**: Argo Workflows

## Prerequisites for HTTPS Ingress

1. **NGINX Ingress Controller**
   ```bash
   helm install ingress-nginx ingress-nginx/ingress-nginx \
     --namespace ingress-nginx \
     --create-namespace \
     --set controller.service.loadBalancerIP=4.149.83.133
   ```

2. **cert-manager**
   ```bash
   kubectl apply -f https://github.com/cert-manager/cert-manager/releases/download/v1.13.3/cert-manager.yaml
   ```

3. **DNS Configuration**
   - cloudhealthoffice.com → 4.149.83.133
   - www.cloudhealthoffice.com → 4.149.83.133
   - portal.cloudhealthoffice.com → 4.149.83.133

## Deployment Order

1. **Namespaces and RBAC**
   ```bash
   kubectl apply -f namespaces.yaml
   kubectl apply -f rbac/
   ```

2. **Secrets and ConfigMaps**
   ```bash
   kubectl apply -f secrets/
   kubectl apply -f configmaps/
   ```

3. **Core Services**
   ```bash
   kubectl apply -f backend-api-integration.yaml
   kubectl apply -f coverage-service-deployment.yaml
   kubectl apply -f member-service-deployment.yaml
   # ... other services
   ```

4. **Website and Portal**
   ```bash
   kubectl apply -f ../site/k8s/site-deployment.yaml
   kubectl apply -f ../portal/CloudHealthOffice.Portal/k8s/portal-deployment.yaml
   ```

5. **HTTPS Ingress (NEW)**
   ```bash
   kubectl apply -f cert-manager-issuer.yaml
   kubectl apply -f site-ingress.yaml
   kubectl apply -f portal-ingress.yaml
   ```

## Service Architecture

### External Access (via Ingress)
- **Website**: https://cloudhealthoffice.com
- **Portal**: https://portal.cloudhealthoffice.com

### Internal Services (ClusterIP)
All microservices use ClusterIP and are accessed internally:
- member-service.cloudhealthoffice:3000
- coverage-service.cloudhealthoffice:3001
- claims-service.cloudhealthoffice:3002
- eligibility-service.cloudhealthoffice:3003
- authorization-service.cloudhealthoffice:3004
- provider-service.cloudhealthoffice:3005
- benefit-plan-service.cloudhealthoffice:3006

## HIPAA X12 Workflows

EDI transaction processing jobs:
- **275**: Attachment submission (upload-job)
- **277**: Claims status (download-job)
- **278**: Prior authorization (upload-job)
- **837**: Claims submission (claims-jobs)

## Monitoring

Check deployment status:
```bash
# All pods
kubectl get pods -n cloudhealthoffice

# Ingress status
kubectl get ingress -n cloudhealthoffice

# Certificates
kubectl get certificate -n cloudhealthoffice

# Services
kubectl get svc -n cloudhealthoffice
```

## Troubleshooting

### Ingress Issues
See [INGRESS-HTTPS-SETUP.md](./INGRESS-HTTPS-SETUP.md#troubleshooting) for:
- Certificate not issuing
- DNS configuration
- 502 Bad Gateway errors
- TLS certificate renewal

### Common Commands
```bash
# View ingress logs
kubectl logs -n ingress-nginx -l app.kubernetes.io/component=controller

# View cert-manager logs
kubectl logs -n cert-manager -l app=cert-manager

# Describe certificate
kubectl describe certificate <cert-name> -n cloudhealthoffice

# Test HTTPS
curl -I https://cloudhealthoffice.com
```

## Security

### HIPAA Compliance Features
- TLS 1.2+ encryption (automatic via NGINX ingress)
- Strict-Transport-Security headers (HSTS)
- X-Frame-Options, X-Content-Type-Options headers
- Automated certificate rotation (cert-manager)
- Network policies for service isolation

### Secret Management
- Secrets stored in Kubernetes Secrets
- Integration with Azure Key Vault (optional)
- RBAC controls for secret access

## Additional Resources

- **Website Deployment**: `../site/DEPLOYMENT.md`
- **Portal Deployment**: `../portal/CloudHealthOffice.Portal/README.md`
- **HIPAA X12 Workflows**: `./SFTP-WORKFLOWS-SUMMARY.md`
- **Main Documentation**: `../README.md`

## Support

For issues or questions:
1. Check [INGRESS-HTTPS-SETUP.md](./INGRESS-HTTPS-SETUP.md#troubleshooting)
2. Review logs: `kubectl logs -n <namespace> <pod-name>`
3. Check pod status: `kubectl describe pod <pod-name> -n <namespace>`
4. Review ingress events: `kubectl describe ingress <ingress-name> -n cloudhealthoffice`
