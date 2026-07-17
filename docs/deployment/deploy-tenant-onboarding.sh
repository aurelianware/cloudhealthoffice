#!/bin/bash
set -e

echo "=========================================="
echo "Tenant Onboarding Workflow Deployment"
echo "=========================================="
echo ""

# Check prerequisites
if ! command -v kubectl &> /dev/null; then
    echo "❌ kubectl is not installed"
    exit 1
fi

if ! command -v argo &> /dev/null; then
    echo "⚠️  Argo CLI not installed - install from https://github.com/argoproj/argo-workflows/releases"
    echo "   Workflow can still be deployed via kubectl"
fi

# Create necessary secrets if they don't exist
echo "📋 Checking required secrets..."

# Check cosmos-db-credentials
if ! kubectl -n cho-workflows get secret cosmos-db-credentials &> /dev/null; then
    echo "Creating cosmos-db-credentials secret..."
    read -p "Cosmos DB Account Name: " COSMOS_ACCOUNT
    read -p "Cosmos DB Resource Group: " COSMOS_RG
    
    kubectl -n cho-workflows create secret generic cosmos-db-credentials \
        --from-literal=account-name="$COSMOS_ACCOUNT" \
        --from-literal=resource-group="$COSMOS_RG"
fi

# Check azure-service-principal
if ! kubectl -n cho-workflows get secret azure-service-principal &> /dev/null; then
    echo "⚠️  azure-service-principal secret not found"
    echo "   Create it with: kubectl create secret generic azure-service-principal -n cho-workflows --from-literal=client-id=... --from-literal=client-secret=..."
fi

# Check stripe-api-keys
if ! kubectl -n cho-workflows get secret stripe-api-keys &> /dev/null; then
    echo "⚠️  stripe-api-keys secret not found (optional for now)"
fi

# Check tenant-service-credentials
if ! kubectl -n cho-workflows get secret tenant-service-credentials &> /dev/null; then
    echo "Creating tenant-service-credentials secret..."
    API_KEY=$(openssl rand -hex 32)
    
    kubectl -n cho-workflows create secret generic tenant-service-credentials \
        --from-literal=api-key="$API_KEY"
    
    echo "✅ Generated API key: $API_KEY"
fi

# Deploy the workflow template
echo ""
echo "📦 Deploying tenant onboarding workflow..."

kubectl apply -f argo-workflows/tenant-onboarding.yaml

echo "✅ Workflow template deployed successfully"

# Test the workflow (optional)
read -p "Would you like to test the workflow with a demo tenant? (y/N): " TEST_WORKFLOW

if [[ "$TEST_WORKFLOW" =~ ^[Yy]$ ]]; then
    echo ""
    echo "🧪 Submitting test workflow..."
    
    if command -v argo &> /dev/null; then
        argo submit -n cho-workflows argo-workflows/tenant-onboarding.yaml \
            --parameter tenant-id="demo-health-plan-$(date +%s)" \
            --parameter tenant-name="Demo Health Plan" \
            --parameter organization-name="Demo Health Insurance Co." \
            --parameter admin-email="admin@demo-health.com" \
            --parameter admin-name="John Demo" \
            --parameter subscription-tier="starter" \
            --parameter enabled-modules="claims,eligibility" \
            --parameter phone="+1-555-0100" \
            --watch
    else
        echo "Argo CLI not installed - submit workflow manually or via portal"
    fi
fi

echo ""
echo "=========================================="
echo "✅ Deployment Complete!"
echo "=========================================="
echo ""
echo "Next steps:"
echo "  1. Verify workflow template:"
echo "     kubectl get workflowtemplates -n cho-workflows"
echo ""
echo "  2. Watch for new tenant signups:"
echo "     argo list -n cho-workflows --watch"
echo ""
echo "  3. View workflow logs:"
echo "     argo logs -n cho-workflows <workflow-name>"
echo ""
echo "  4. Test signup at your deployed portal, for example: https://portal.<your-domain>/signup"
echo ""
