#!/bin/bash
# Check status of most recent tenant onboarding workflow

echo "🔍 Checking recent tenant onboarding workflows..."
echo ""

# Get the most recent workflow
LATEST_WORKFLOW=$(kubectl get workflows -n cho-workflows \
  --sort-by=.metadata.creationTimestamp \
  -o jsonpath='{.items[-1:].metadata.name}' 2>/dev/null)

if [ -z "$LATEST_WORKFLOW" ]; then
  echo "❌ No workflows found"
  echo ""
  echo "Tip: Workflows are created when you submit the signup form"
  exit 1
fi

echo "📋 Latest Workflow: $LATEST_WORKFLOW"
echo ""

# Get workflow status
kubectl get workflow "$LATEST_WORKFLOW" -n cho-workflows -o wide

echo ""
echo "📊 Workflow Steps:"
kubectl get workflow "$LATEST_WORKFLOW" -n cho-workflows \
  -o jsonpath='{range .status.nodes[*]}{.displayName}{"\t"}{.phase}{"\t"}{.message}{"\n"}{end}' | \
  column -t -s $'\t'

echo ""
echo "💳 Stripe Subscription Step Details:"
kubectl get workflow "$LATEST_WORKFLOW" -n cho-workflows \
  -o jsonpath='{.status.nodes[?(@.displayName=="setup-stripe-billing")]}' | jq -r '.message // "Pending..."'

echo ""
echo "📜 View full logs:"
echo "  kubectl logs -n cho-workflows -l workflows.argoproj.io/workflow=$LATEST_WORKFLOW --tail=100"
echo ""
echo "🔄 Watch workflow progress:"
echo "  kubectl get workflow $LATEST_WORKFLOW -n cho-workflows -w"
