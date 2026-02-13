#!/bin/bash

# Update all K8s deployments to use GitHub Container Registry images
# Run this after Docker images are built and pushed

set -e

REGISTRY="ghcr.io/aurelianware/cloudhealthoffice"

echo "Updating Kubernetes deployments to use GitHub Container Registry images..."
echo "Registry: $REGISTRY"
echo ""

# Services with existing deployments
SERVICES=(
  "claims-service"
  "eligibility-service"
  "provider-service"
  "authorization-service"
  "benefit-plan-service"
  "reference-data-service"
)

for service in "${SERVICES[@]}"; do
  deployment_file="services/$service/k8s/$service-deployment.yaml"
  
  if [ -f "$deployment_file" ]; then
    echo "Updating $service..."
    
    # Update image reference
    sed -i.bak "s|image:.*$service.*|image: $REGISTRY-$service:latest|g" "$deployment_file"
    
    # Remove backup file
    rm -f "$deployment_file.bak"
    
    echo "✓ $service deployment updated"
  else
    echo "⚠️  Deployment file not found: $deployment_file"
  fi
done

echo ""
echo "Deployment files updated successfully!"
echo ""
echo "Next steps:"
echo "1. Verify GitHub Actions build completed: https://github.com/aurelianware/cloudhealthoffice/actions"
echo "2. Deploy updated services: kubectl apply -f services/*/k8s/"
echo "3. Watch rollout: kubectl rollout status deployment -n cloudhealthoffice"
echo "4. Verify pods: kubectl get pods -n cloudhealthoffice"
