#!/bin/bash
# HTTPS Ingress Validation Script for Cloud Health Office
# This script validates the ingress, certificate, and DNS configuration

set -e

# Colors for output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
BLUE='\033[0;34m'
NC='\033[0m' # No Color

# Configuration
NAMESPACE="cloudhealthoffice"
INGRESS_NAMESPACE="ingress-nginx"
CERTMANAGER_NAMESPACE="cert-manager"
EXPECTED_IP="4.149.83.133"

echo -e "${BLUE}╔════════════════════════════════════════════════════════════════╗${NC}"
echo -e "${BLUE}║  Cloud Health Office - HTTPS Ingress Validation              ║${NC}"
echo -e "${BLUE}╚════════════════════════════════════════════════════════════════╝${NC}"
echo ""

# Function to print status
print_status() {
    local status=$1
    local message=$2
    if [ "$status" = "OK" ]; then
        echo -e "${GREEN}✓${NC} $message"
    elif [ "$status" = "WARN" ]; then
        echo -e "${YELLOW}⚠${NC} $message"
    elif [ "$status" = "FAIL" ]; then
        echo -e "${RED}✗${NC} $message"
    else
        echo -e "${BLUE}ℹ${NC} $message"
    fi
}

# Check if kubectl is available
echo -e "\n${BLUE}[1/9] Checking Prerequisites${NC}"
if ! command -v kubectl &> /dev/null; then
    print_status "FAIL" "kubectl not found. Please install kubectl first."
    exit 1
fi
print_status "OK" "kubectl is installed"

# Check cluster connection
if ! kubectl cluster-info &> /dev/null; then
    print_status "FAIL" "Cannot connect to Kubernetes cluster"
    exit 1
fi
print_status "OK" "Connected to Kubernetes cluster"

# Check NGINX Ingress Controller
echo -e "\n${BLUE}[2/9] Checking NGINX Ingress Controller${NC}"
if ! kubectl get namespace "$INGRESS_NAMESPACE" &> /dev/null; then
    print_status "FAIL" "Namespace $INGRESS_NAMESPACE not found"
    print_status "INFO" "Run: helm install ingress-nginx ingress-nginx/ingress-nginx --namespace ingress-nginx --create-namespace"
    exit 1
fi
print_status "OK" "Namespace $INGRESS_NAMESPACE exists"

# Check ingress controller deployment
if ! kubectl get deployment -n "$INGRESS_NAMESPACE" ingress-nginx-controller &> /dev/null; then
    print_status "FAIL" "NGINX Ingress Controller deployment not found"
    exit 1
fi

INGRESS_REPLICAS=$(kubectl get deployment -n "$INGRESS_NAMESPACE" ingress-nginx-controller -o jsonpath='{.status.readyReplicas}')
if [ "$INGRESS_REPLICAS" -ge 1 ]; then
    print_status "OK" "NGINX Ingress Controller is running ($INGRESS_REPLICAS replicas ready)"
else
    print_status "FAIL" "NGINX Ingress Controller is not ready"
    exit 1
fi

# Check LoadBalancer IP
INGRESS_IP=$(kubectl get svc -n "$INGRESS_NAMESPACE" ingress-nginx-controller -o jsonpath='{.status.loadBalancer.ingress[0].ip}')
if [ -z "$INGRESS_IP" ]; then
    print_status "WARN" "LoadBalancer IP not assigned yet (this may take a few minutes)"
else
    if [ "$INGRESS_IP" = "$EXPECTED_IP" ]; then
        print_status "OK" "LoadBalancer IP: $INGRESS_IP (matches expected)"
    else
        print_status "WARN" "LoadBalancer IP: $INGRESS_IP (expected: $EXPECTED_IP)"
    fi
fi

# Check cert-manager
echo -e "\n${BLUE}[3/9] Checking cert-manager${NC}"
if ! kubectl get namespace "$CERTMANAGER_NAMESPACE" &> /dev/null; then
    print_status "FAIL" "Namespace $CERTMANAGER_NAMESPACE not found"
    print_status "INFO" "Run: kubectl apply -f https://github.com/cert-manager/cert-manager/releases/download/v1.13.3/cert-manager.yaml"
    exit 1
fi
print_status "OK" "Namespace $CERTMANAGER_NAMESPACE exists"

# Check cert-manager pods
CERTMANAGER_PODS=$(kubectl get pods -n "$CERTMANAGER_NAMESPACE" -o jsonpath='{.items[*].status.phase}' | grep -o "Running" | wc -l)
if [ "$CERTMANAGER_PODS" -ge 3 ]; then
    print_status "OK" "cert-manager pods are running ($CERTMANAGER_PODS/3)"
else
    print_status "WARN" "Not all cert-manager pods are running ($CERTMANAGER_PODS/3)"
fi

# Check ClusterIssuer
echo -e "\n${BLUE}[4/9] Checking ClusterIssuers${NC}"
if kubectl get clusterissuer letsencrypt-prod &> /dev/null; then
    print_status "OK" "ClusterIssuer 'letsencrypt-prod' exists"
else
    print_status "FAIL" "ClusterIssuer 'letsencrypt-prod' not found"
    print_status "INFO" "Run: kubectl apply -f k8s/cert-manager-issuer.yaml"
fi

if kubectl get clusterissuer letsencrypt-staging &> /dev/null; then
    print_status "OK" "ClusterIssuer 'letsencrypt-staging' exists"
else
    print_status "WARN" "ClusterIssuer 'letsencrypt-staging' not found (optional)"
fi

# Check namespace
echo -e "\n${BLUE}[5/9] Checking Application Namespace${NC}"
if ! kubectl get namespace "$NAMESPACE" &> /dev/null; then
    print_status "FAIL" "Namespace $NAMESPACE not found"
    print_status "INFO" "Run: kubectl apply -f k8s/namespaces.yaml"
    exit 1
fi
print_status "OK" "Namespace $NAMESPACE exists"

# Check services
echo -e "\n${BLUE}[6/9] Checking Services${NC}"
if kubectl get svc -n "$NAMESPACE" site &> /dev/null; then
    SERVICE_TYPE=$(kubectl get svc -n "$NAMESPACE" site -o jsonpath='{.spec.type}')
    if [ "$SERVICE_TYPE" = "ClusterIP" ]; then
        print_status "OK" "Service 'site' exists (type: ClusterIP)"
    else
        print_status "WARN" "Service 'site' type is $SERVICE_TYPE (expected: ClusterIP)"
    fi
else
    print_status "FAIL" "Service 'site' not found"
fi

if kubectl get svc -n "$NAMESPACE" portal &> /dev/null; then
    SERVICE_TYPE=$(kubectl get svc -n "$NAMESPACE" portal -o jsonpath='{.spec.type}')
    if [ "$SERVICE_TYPE" = "ClusterIP" ]; then
        print_status "OK" "Service 'portal' exists (type: ClusterIP)"
    else
        print_status "WARN" "Service 'portal' type is $SERVICE_TYPE (expected: ClusterIP)"
    fi
else
    print_status "FAIL" "Service 'portal' not found"
fi

# Check ingress resources
echo -e "\n${BLUE}[7/9] Checking Ingress Resources${NC}"
if kubectl get ingress -n "$NAMESPACE" site-ingress &> /dev/null; then
    print_status "OK" "Ingress 'site-ingress' exists"
    
    # Check hosts
    HOSTS=$(kubectl get ingress -n "$NAMESPACE" site-ingress -o jsonpath='{.spec.rules[*].host}')
    print_status "INFO" "Configured hosts: $HOSTS"
else
    print_status "FAIL" "Ingress 'site-ingress' not found"
    print_status "INFO" "Run: kubectl apply -f k8s/site-ingress.yaml"
fi

if kubectl get ingress -n "$NAMESPACE" portal-ingress &> /dev/null; then
    print_status "OK" "Ingress 'portal-ingress' exists"
    
    # Check hosts
    HOSTS=$(kubectl get ingress -n "$NAMESPACE" portal-ingress -o jsonpath='{.spec.rules[*].host}')
    print_status "INFO" "Configured hosts: $HOSTS"
else
    print_status "FAIL" "Ingress 'portal-ingress' not found"
    print_status "INFO" "Run: kubectl apply -f k8s/portal-ingress.yaml"
fi

# Check certificates
echo -e "\n${BLUE}[8/9] Checking TLS Certificates${NC}"
if kubectl get certificate -n "$NAMESPACE" site-tls-secret &> /dev/null; then
    CERT_READY=$(kubectl get certificate -n "$NAMESPACE" site-tls-secret -o jsonpath='{.status.conditions[?(@.type=="Ready")].status}')
    if [ "$CERT_READY" = "True" ]; then
        print_status "OK" "Certificate 'site-tls-secret' is ready"
    else
        print_status "WARN" "Certificate 'site-tls-secret' is not ready yet (status: $CERT_READY)"
        print_status "INFO" "Check: kubectl describe certificate site-tls-secret -n $NAMESPACE"
    fi
else
    print_status "WARN" "Certificate 'site-tls-secret' not found (will be created automatically)"
fi

if kubectl get certificate -n "$NAMESPACE" portal-tls-secret &> /dev/null; then
    CERT_READY=$(kubectl get certificate -n "$NAMESPACE" portal-tls-secret -o jsonpath='{.status.conditions[?(@.type=="Ready")].status}')
    if [ "$CERT_READY" = "True" ]; then
        print_status "OK" "Certificate 'portal-tls-secret' is ready"
    else
        print_status "WARN" "Certificate 'portal-tls-secret' is not ready yet (status: $CERT_READY)"
        print_status "INFO" "Check: kubectl describe certificate portal-tls-secret -n $NAMESPACE"
    fi
else
    print_status "WARN" "Certificate 'portal-tls-secret' not found (will be created automatically)"
fi

# Check DNS (if dig is available)
echo -e "\n${BLUE}[9/9] Checking DNS Configuration${NC}"
if command -v dig &> /dev/null; then
    DOMAINS=("cloudhealthoffice.com" "www.cloudhealthoffice.com" "portal.cloudhealthoffice.com")
    
    for DOMAIN in "${DOMAINS[@]}"; do
        DNS_IP=$(dig +short "$DOMAIN" | grep -E '^[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+$' | head -n1)
        if [ -z "$DNS_IP" ]; then
            print_status "WARN" "DNS for $DOMAIN not resolving"
        elif [ "$DNS_IP" = "$EXPECTED_IP" ] || [ "$DNS_IP" = "$INGRESS_IP" ]; then
            print_status "OK" "DNS for $DOMAIN → $DNS_IP"
        else
            print_status "WARN" "DNS for $DOMAIN → $DNS_IP (expected: $INGRESS_IP or $EXPECTED_IP)"
        fi
    done
else
    print_status "INFO" "dig not available, skipping DNS checks"
    print_status "INFO" "Install dig to enable DNS validation"
fi

# Summary
echo -e "\n${BLUE}╔════════════════════════════════════════════════════════════════╗${NC}"
echo -e "${BLUE}║  Validation Summary                                           ║${NC}"
echo -e "${BLUE}╚════════════════════════════════════════════════════════════════╝${NC}"
echo ""

echo "Next steps:"
echo "1. If any components are missing, follow the installation guide:"
echo "   → k8s/QUICKSTART-HTTPS.md"
echo ""
echo "2. Monitor certificate issuance:"
echo "   → kubectl get certificate -n $NAMESPACE -w"
echo ""
echo "3. View detailed certificate status:"
echo "   → kubectl describe certificate site-tls-secret -n $NAMESPACE"
echo "   → kubectl describe certificate portal-tls-secret -n $NAMESPACE"
echo ""
echo "4. Check cert-manager logs if issues occur:"
echo "   → kubectl logs -n cert-manager -l app=cert-manager --tail=50"
echo ""
echo "5. Test HTTPS access:"
echo "   → curl -I https://cloudhealthoffice.com"
echo "   → curl -I https://portal.cloudhealthoffice.com"
echo ""

echo -e "${GREEN}Validation complete!${NC}"
