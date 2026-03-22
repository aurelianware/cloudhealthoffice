#!/bin/bash
# Deploy SFTP Server to Kubernetes Cluster
# Cloud Health Office - EDI Integration Dependency

set -euo pipefail

echo "=========================================="
echo "Cloud Health Office - SFTP Server Setup"
echo "=========================================="
echo ""

# Check if kubectl is available
if ! command -v kubectl &> /dev/null; then
    echo "❌ kubectl not found. Please install kubectl first."
    exit 1
fi

# Check cluster connection
if ! kubectl cluster-info &> /dev/null; then
    echo "❌ Cannot connect to Kubernetes cluster"
    echo "Run: kubectl config current-context"
    exit 1
fi

CURRENT_CONTEXT=$(kubectl config current-context)
echo "📍 Current cluster: $CURRENT_CONTEXT"
echo ""

read -p "Deploy SFTP server to this cluster? (y/n) " -n 1 -r
echo
if [[ ! $REPLY =~ ^[Yy]$ ]]; then
    echo "Aborted."
    exit 0
fi

echo ""
echo "Step 1: Creating namespace and resources..."
kubectl apply -f k8s/sftp-server-deployment.yaml

echo ""
echo "Step 2: Waiting for SSH host key generation..."
kubectl wait --for=condition=complete --timeout=60s job/generate-ssh-keys -n cho-sftp || {
    echo "⚠️  Key generation job didn't complete. Check logs:"
    echo "   kubectl logs -n cho-sftp job/generate-ssh-keys"
}

echo ""
echo "Step 3: Starting SFTP server..."
kubectl rollout status deployment/sftp-server -n cho-sftp --timeout=120s

echo ""
echo "=========================================="
echo "✅ SFTP Server Deployed Successfully!"
echo "=========================================="
echo ""

# Get service details
SFTP_IP=$(kubectl get svc sftp-service -n cho-sftp -o jsonpath='{.status.loadBalancer.ingress[0].ip}' 2>/dev/null || echo "pending")
SFTP_PORT=$(kubectl get svc sftp-service -n cho-sftp -o jsonpath='{.spec.ports[0].port}')

if [ "$SFTP_IP" = "pending" ] || [ -z "$SFTP_IP" ]; then
    echo "⏳ LoadBalancer IP pending. To get the IP later, run:"
    echo "   kubectl get svc sftp-service -n cho-sftp"
    echo ""
    echo "For local testing, use port forwarding:"
    echo "   kubectl port-forward -n cho-sftp svc/sftp-service 2222:22"
    echo "   sftp -P 2222 cho-edi@localhost"
else
    echo "🌐 SFTP Server Address: sftp://$SFTP_IP:$SFTP_PORT"
    echo ""
    echo "📋 Default Credentials:"
    echo "   Username: cho-edi"
    echo "   Password: changeme123"
    echo "   Directory: /home/cho-edi/upload"
    echo ""
    echo "   Username: clearinghouse"
    echo "   Password: changeme456"
    echo "   Directory: /home/clearinghouse/edi"
fi

echo ""
echo "⚠️  IMPORTANT: Change default passwords for production!"
echo "Edit k8s/sftp-server-deployment.yaml and update the Secret:"
echo "   kubectl edit secret sftp-users -n cho-sftp"
echo ""

echo "🧪 Test connection:"
if [ "$SFTP_IP" = "pending" ] || [ -z "$SFTP_IP" ]; then
    echo "   # Start port forward in another terminal:"
    echo "   kubectl port-forward -n cho-sftp svc/sftp-service 2222:22"
    echo ""
    echo "   # Then connect:"
    echo "   sftp -P 2222 cho-edi@localhost"
else
    echo "   sftp -P $SFTP_PORT cho-edi@$SFTP_IP"
fi

echo ""
echo "📊 View logs:"
echo "   kubectl logs -n cho-sftp -l app=sftp-server -f"
echo ""
echo "🗑️  To remove:"
echo "   kubectl delete namespace cho-sftp"
echo ""
