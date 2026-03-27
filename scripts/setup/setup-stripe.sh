#!/bin/bash
set -e

echo "=========================================="
echo "Stripe Payment Configuration"
echo "=========================================="
echo ""

# Check if Stripe CLI is installed
if ! command -v stripe &> /dev/null; then
    echo "⚠️  Stripe CLI not installed"
    echo "   Install: brew install stripe/stripe-cli/stripe"
    echo ""
    read -p "Continue without CLI? (y/N): " CONTINUE
    if [[ ! "$CONTINUE" =~ ^[Yy]$ ]]; then
        exit 1
    fi
fi

echo "Choose environment:"
echo "1) Test mode (recommended for development)"
echo "2) Live mode (production)"
read -p "Enter choice (1 or 2): " ENV_CHOICE

if [ "$ENV_CHOICE" = "2" ]; then
    echo ""
    echo "⚠️  LIVE MODE - Real charges will be processed!"
    read -p "Are you sure? (yes/NO): " CONFIRM
    if [ "$CONFIRM" != "yes" ]; then
        echo "Cancelled."
        exit 0
    fi
    MODE="live"
    KEY_PREFIX="sk_live"
    RK_PREFIX="rk_live"
    PK_PREFIX="pk_live"
else
    MODE="test"
    KEY_PREFIX="sk_test"
    RK_PREFIX="rk_test"
    PK_PREFIX="pk_test"
fi

echo ""
echo "Enter your Stripe keys from https://dashboard.stripe.com/apikeys"
echo ""

read -p "Secret Key ($KEY_PREFIX... or $RK_PREFIX...): " SECRET_KEY
read -p "Publishable Key ($PK_PREFIX...): " PUBLISHABLE_KEY

# Validate key format (accept both standard and restricted keys)
if [[ ! "$SECRET_KEY" =~ ^$KEY_PREFIX ]] && [[ ! "$SECRET_KEY" =~ ^$RK_PREFIX ]]; then
    echo "❌ Invalid secret key format (should start with $KEY_PREFIX or $RK_PREFIX for restricted keys)"
    exit 1
fi

if [[ ! "$PUBLISHABLE_KEY" =~ ^$PK_PREFIX ]]; then
    echo "❌ Invalid publishable key format (should start with $PK_PREFIX)"
    exit 1
fi

echo ""
echo "Creating Stripe products and prices..."
echo ""

if command -v stripe &> /dev/null; then
    # Use Stripe CLI to create products
    stripe login --api-key "$SECRET_KEY"
    
    # Create Starter tier
    STARTER_PRODUCT=$(stripe products create \
        --name "Cloud Health Office - Starter" \
        --description "Up to 10,000 claims/month" \
        --format json | jq -r '.id')
    
    STARTER_PRICE=$(stripe prices create \
        --product "$STARTER_PRODUCT" \
        --unit-amount 49900 \
        --currency usd \
        --recurring[interval]=month \
        --recurring[trial_period_days]=14 \
        --format json | jq -r '.id')
    
    echo "✅ Starter tier created: $STARTER_PRICE"
    
    # Create Professional tier
    PROFESSIONAL_PRODUCT=$(stripe products create \
        --name "Cloud Health Office - Professional" \
        --description "Up to 50,000 claims/month" \
        --format json | jq -r '.id')
    
    PROFESSIONAL_PRICE=$(stripe prices create \
        --product "$PROFESSIONAL_PRODUCT" \
        --unit-amount 149900 \
        --currency usd \
        --recurring[interval]=month \
        --recurring[trial_period_days]=14 \
        --format json | jq -r '.id')
    
    echo "✅ Professional tier created: $PROFESSIONAL_PRICE"
    
    # Create Enterprise tier (custom pricing)
    ENTERPRISE_PRODUCT=$(stripe products create \
        --name "Cloud Health Office - Enterprise" \
        --description "Unlimited claims, dedicated infrastructure" \
        --format json | jq -r '.id')
    
    echo "✅ Enterprise tier created: $ENTERPRISE_PRODUCT (custom pricing)"
else
    echo "ℹ️  Create products manually at https://dashboard.stripe.com/products"
    echo ""
    echo "Starter tier:"
    echo "  - Name: Cloud Health Office - Starter"
    echo "  - Price: See internal pricing documentation"
    echo ""
    echo "Professional tier:"
    echo "  - Name: Cloud Health Office - Professional"
    echo "  - Price: See internal pricing documentation"
    echo ""
    read -p "Starter price ID (price_...): " STARTER_PRICE
    read -p "Professional price ID (price_...): " PROFESSIONAL_PRICE
fi

echo ""
echo "📦 Creating Kubernetes secrets..."

# Create Stripe secret for workflows
kubectl create secret generic stripe-api-keys -n cho-workflows \
    --from-literal=secret-key="$SECRET_KEY" \
    --from-literal=publishable-key="$PUBLISHABLE_KEY" \
    --from-literal=starter-price-id="$STARTER_PRICE" \
    --from-literal=professional-price-id="$PROFESSIONAL_PRICE" \
    --dry-run=client -o yaml | kubectl apply -f -

echo "✅ Created secret: stripe-api-keys (cho-workflows namespace)"

# Create Stripe secret for portal
kubectl create secret generic stripe-api-keys -n cloudhealthoffice \
    --from-literal=secret-key="$SECRET_KEY" \
    --from-literal=publishable-key="$PUBLISHABLE_KEY" \
    --from-literal=starter-price-id="$STARTER_PRICE" \
    --from-literal=professional-price-id="$PROFESSIONAL_PRICE" \
    --dry-run=client -o yaml | kubectl apply -f -

echo "✅ Created secret: stripe-api-keys (cloudhealthoffice namespace)"

echo ""
echo "🔗 Setting up webhooks..."
echo ""
echo "Configure webhook endpoint at: https://dashboard.stripe.com/webhooks"
echo "Webhook URL: https://api.cloudhealthoffice.com/v1/webhooks/stripe"
echo ""
echo "Select these events:"
echo "  ✓ customer.subscription.created"
echo "  ✓ customer.subscription.updated"
echo "  ✓ customer.subscription.deleted"
echo "  ✓ invoice.payment_succeeded"
echo "  ✓ invoice.payment_failed"
echo ""
read -p "Webhook signing secret (whsec_...): " WEBHOOK_SECRET

if [[ "$WEBHOOK_SECRET" =~ ^whsec_ ]]; then
    kubectl create secret generic stripe-webhook-secret -n cloudhealthoffice \
        --from-literal=signing-secret="$WEBHOOK_SECRET" \
        --dry-run=client -o yaml | kubectl apply -f -
    
    echo "✅ Created secret: stripe-webhook-secret"
fi

echo ""
echo "📝 Updating portal configuration..."

# Update appsettings.json
cat > /tmp/stripe-config.json <<EOF
{
  "Stripe": {
    "PublishableKey": "$PUBLISHABLE_KEY",
    "SecretKey": "$SECRET_KEY",
    "PricingTiers": {
      "starter": "$STARTER_PRICE",
      "professional": "$PROFESSIONAL_PRICE",
      "enterprise": "custom"
    },
    "TrialPeriodDays": 14
  }
}
EOF

echo ""
echo "Add this to portal/CloudHealthOffice.Portal/appsettings.json:"
cat /tmp/stripe-config.json

echo ""
echo "=========================================="
echo "✅ Stripe Configuration Complete!"
echo "=========================================="
echo ""
echo "Next steps:"
echo "  1. Update appsettings.json with Stripe configuration"
echo "  2. Deploy portal: kubectl rollout restart deployment/portal -n cloudhealthoffice"
echo "  3. Test signup at: https://portal.cloudhealthoffice.com/signup"
echo ""
echo "Test cards ($MODE mode):"
echo "  ✅ Success: 4242 4242 4242 4242"
echo "  ❌ Decline: 4000 0000 0000 0002"
echo "  ⚠️  Requires auth: 4000 0025 0000 3155"
echo ""
echo "Monitor payments: https://dashboard.stripe.com/payments"
echo ""
