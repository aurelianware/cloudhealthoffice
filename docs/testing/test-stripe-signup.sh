#!/bin/bash
# Test Stripe signup flow and monitor workflow execution

set -e

PORTAL_BASE_URL="${PORTAL_BASE_URL:-http://localhost:5026}"

echo "🚀 Cloud Health Office - Stripe Signup Test"
echo "=========================================="
echo ""
echo "📋 Test Instructions:"
echo "1. Open: ${PORTAL_BASE_URL}/signup"
echo "2. Fill in organization details:"
echo "   - Organization: Test Health Plan $(date +%s)"
echo "   - Tenant Name: test-$(date +%s)"
echo "   - Contact Name: Test Admin"
echo "   - Email: test@example.com"
echo "   - Phone: 555-0100"
echo ""
echo "3. Select Subscription Tier: Starter or Professional"
echo ""
echo "4. Payment Information:"
echo "   - Card Number: 4242 4242 4242 4242"
echo "   - Expiration: 12/28"
echo "   - CVC: 123"
echo "   - Billing Name: Test Admin"
echo ""
echo "5. Select modules (claims, eligibility, etc.)"
echo "6. Click 'Create Account'"
echo ""
echo "=========================================="
echo ""
echo "⏳ Monitoring workflows... (Press Ctrl+C to stop)"
echo ""

# Watch for new tenant onboarding workflows
kubectl get workflows -n cho-workflows -w --sort-by=.metadata.creationTimestamp | \
  grep --line-buffered tenant-onboarding || true
