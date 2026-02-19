#!/usr/bin/env bash
# setup-vault.sh - Initialize and configure HashiCorp Vault for Cloud Health Office
# This script sets up Vault with authentication methods, policies, and initial secrets

set -euo pipefail

# Colors for output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
BLUE='\033[0;34m'
NC='\033[0m' # No Color

# Print colored output
print_info() {
    echo -e "${BLUE}ℹ ${1}${NC}"
}

print_success() {
    echo -e "${GREEN}✓ ${1}${NC}"
}

print_warning() {
    echo -e "${YELLOW}⚠ ${1}${NC}"
}

print_error() {
    echo -e "${RED}✗ ${1}${NC}"
}

# Check prerequisites
check_prerequisites() {
    print_info "Checking prerequisites..."
    
    if ! command -v vault &> /dev/null; then
        print_error "Vault CLI not found. Please install from https://www.vaultproject.io/downloads"
        exit 1
    fi
    
    if ! command -v kubectl &> /dev/null; then
        print_error "kubectl not found. Please install kubectl"
        exit 1
    fi
    
    if ! command -v jq &> /dev/null; then
        print_error "jq not found. Please install jq for JSON processing"
        exit 1
    fi
    
    print_success "All prerequisites met"
}

# Initialize Vault
initialize_vault() {
    print_info "Initializing Vault..."
    
    # Check if Vault is already initialized
    if vault status 2>/dev/null | grep -q "Initialized.*true"; then
        print_warning "Vault is already initialized"
        return 0
    fi
    
    # Initialize with 5 key shares and 3 key threshold
    print_info "Initializing Vault with 5 key shares (threshold: 3)..."
    
    kubectl exec -n vault vault-0 -- vault operator init \
        -key-shares=5 \
        -key-threshold=3 \
        -format=json > vault-keys.json
    
    print_success "Vault initialized successfully"
    print_warning "⚠️  IMPORTANT: Backup vault-keys.json to secure offline storage!"
    print_warning "⚠️  Store unseal keys and root token in a password manager"
    
    # Encrypt the keys file
    if command -v gpg &> /dev/null; then
        print_info "Encrypting vault-keys.json with GPG..."
        gpg --symmetric --cipher-algo AES256 vault-keys.json
        print_success "Encrypted as vault-keys.json.gpg"
        print_warning "Delete vault-keys.json after verifying gpg file"
    fi
}

# Unseal Vault
unseal_vault() {
    print_info "Unsealing Vault pods..."
    
    if [ ! -f vault-keys.json ]; then
        print_error "vault-keys.json not found. Cannot unseal Vault."
        exit 1
    fi
    
    # Extract unseal keys
    UNSEAL_KEY_1=$(jq -r '.unseal_keys_b64[0]' vault-keys.json)
    UNSEAL_KEY_2=$(jq -r '.unseal_keys_b64[1]' vault-keys.json)
    UNSEAL_KEY_3=$(jq -r '.unseal_keys_b64[2]' vault-keys.json)
    
    # Unseal all Vault pods
    for i in 0 1 2; do
        print_info "Unsealing vault-$i..."
        
        kubectl exec -n vault vault-$i -- vault operator unseal $UNSEAL_KEY_1 > /dev/null
        kubectl exec -n vault vault-$i -- vault operator unseal $UNSEAL_KEY_2 > /dev/null
        kubectl exec -n vault vault-$i -- vault operator unseal $UNSEAL_KEY_3 > /dev/null
        
        print_success "vault-$i unsealed"
    done
    
    print_success "All Vault pods unsealed"
}

# Configure Vault
configure_vault() {
    print_info "Configuring Vault..."
    
    # Export root token
    export VAULT_TOKEN=$(jq -r '.root_token' vault-keys.json)
    export VAULT_ADDR="http://127.0.0.1:8200"
    
    # Port forward to Vault (in background)
    kubectl port-forward -n vault svc/vault 8200:8200 > /dev/null 2>&1 &
    PORT_FORWARD_PID=$!
    sleep 3
    
    # Enable audit logging
    print_info "Enabling audit logging..."
    vault audit enable file file_path=/vault/audit/audit.log || print_warning "Audit already enabled"
    print_success "Audit logging enabled"
    
    # Enable secrets engines
    print_info "Enabling secrets engines..."
    vault secrets enable -path=secret kv-v2 || print_warning "KV v2 already enabled"
    vault secrets enable transit || print_warning "Transit already enabled"
    print_success "Secrets engines enabled"
    
    # Create encryption key for PHI
    print_info "Creating PHI encryption key..."
    vault write -f transit/keys/phi-encryption || print_warning "Encryption key already exists"
    print_success "PHI encryption key created"
    
    # Enable authentication methods
    print_info "Enabling authentication methods..."
    configure_kubernetes_auth
    configure_approle_auth
    
    # Kill port forward
    kill $PORT_FORWARD_PID 2>/dev/null || true
    
    print_success "Vault configuration complete"
}

# Configure Kubernetes authentication
configure_kubernetes_auth() {
    print_info "Configuring Kubernetes authentication..."
    
    vault auth enable kubernetes || print_warning "Kubernetes auth already enabled"
    
    # Get Kubernetes API address
    KUBERNETES_HOST=$(kubectl config view --raw --minify --flatten -o jsonpath='{.clusters[0].cluster.server}')
    
    # Configure Kubernetes auth
    vault write auth/kubernetes/config \
        kubernetes_host="$KUBERNETES_HOST" \
        kubernetes_ca_cert=@/var/run/secrets/kubernetes.io/serviceaccount/ca.crt \
        token_reviewer_jwt=@/var/run/secrets/kubernetes.io/serviceaccount/token \
        disable_local_ca_jwt=false
    
    # Create policy for microservices
    vault policy write cho-microservices - <<EOF
# Allow reading all application secrets
path "secret/data/cloudhealthoffice/*" {
  capabilities = ["read", "list"]
}

# Allow encryption/decryption for PHI
path "transit/encrypt/phi-encryption" {
  capabilities = ["update"]
}

path "transit/decrypt/phi-encryption" {
  capabilities = ["update"]
}

# Allow reading own token
path "auth/token/lookup-self" {
  capabilities = ["read"]
}

# Allow renewing own token
path "auth/token/renew-self" {
  capabilities = ["update"]
}
EOF
    
    # Create Kubernetes role for microservices
    vault write auth/kubernetes/role/cho-microservices \
        bound_service_account_names=cho-service-account,member-service-sa,claims-service-sa,eligibility-service-sa,authorization-service-sa,coverage-service-sa,provider-service-sa,tenant-service-sa,appeals-service-sa,attachment-service-sa,benefit-plan-service-sa,claims-scrubbing-service-sa,enrollment-import-service-sa,payment-service-sa,reference-data-service-sa,sponsor-service-sa,trading-partner-service-sa \
        bound_service_account_namespaces=cho-svcs \
        policies=cho-microservices \
        ttl=1h \
        max_ttl=24h
    
    print_success "Kubernetes authentication configured"
}

# Configure AppRole authentication (for GitHub Actions)
configure_approle_auth() {
    print_info "Configuring AppRole authentication..."
    
    vault auth enable approle || print_warning "AppRole auth already enabled"
    
    # Create policy for CI/CD
    vault policy write cho-cicd - <<EOF
# Allow reading deployment secrets
path "secret/data/deployment/*" {
  capabilities = ["read", "list"]
}

path "secret/data/cloudhealthoffice/sftp/*" {
  capabilities = ["read", "list"]
}

# Allow reading clearinghouse credentials
path "secret/data/cloudhealthoffice/clearinghouse/*" {
  capabilities = ["read", "list"]
}
EOF
    
    # Create AppRole for GitHub Actions
    vault write auth/approle/role/github-actions \
        secret_id_ttl=0 \
        token_ttl=20m \
        token_max_ttl=30m \
        policies=cho-cicd
    
    # Get Role ID
    ROLE_ID=$(vault read -field=role_id auth/approle/role/github-actions/role-id)
    
    # Generate Secret ID
    SECRET_ID=$(vault write -field=secret_id -f auth/approle/role/github-actions/secret-id)
    
    print_success "AppRole authentication configured"
    print_warning "⚠️  Store these credentials in GitHub Secrets:"
    echo -e "  ${BLUE}VAULT_ROLE_ID:${NC} $ROLE_ID"
    echo -e "  ${BLUE}VAULT_SECRET_ID:${NC} $SECRET_ID"
}

# Populate initial secrets
populate_secrets() {
    print_info "Populating initial secrets..."
    
    # Export root token
    export VAULT_TOKEN=$(jq -r '.root_token' vault-keys.json)
    export VAULT_ADDR="http://127.0.0.1:8200"
    
    # Port forward to Vault (in background)
    kubectl port-forward -n vault svc/vault 8200:8200 > /dev/null 2>&1 &
    PORT_FORWARD_PID=$!
    sleep 3
    
    # Prompt for secrets
    read -p "Enter Cosmos DB connection string (or press Enter to skip): " COSMOS_CONNECTION_STRING
    if [ -n "$COSMOS_CONNECTION_STRING" ]; then
        vault kv put secret/cloudhealthoffice/cosmosdb \
            connection-string="$COSMOS_CONNECTION_STRING"
        print_success "Cosmos DB secret stored"
    fi
    
    read -p "Enter SFTP host (or press Enter to skip): " SFTP_HOST
    if [ -n "$SFTP_HOST" ]; then
        read -p "Enter SFTP username: " SFTP_USERNAME
        read -sp "Enter SFTP password: " SFTP_PASSWORD
        echo
        
        vault kv put secret/cloudhealthoffice/sftp/default \
            host="$SFTP_HOST" \
            username="$SFTP_USERNAME" \
            password="$SFTP_PASSWORD"
        print_success "SFTP credentials stored"
    fi
    
    # Kill port forward
    kill $PORT_FORWARD_PID 2>/dev/null || true
    
    print_success "Initial secrets populated"
}

# Main execution
main() {
    echo "================================================"
    echo "  HashiCorp Vault Setup for Cloud Health Office"
    echo "================================================"
    echo
    
    check_prerequisites
    
    # Check if we're setting up from scratch or just configuring
    if [ ! -f vault-keys.json ]; then
        initialize_vault
        unseal_vault
        configure_vault
        populate_secrets
    else
        print_warning "vault-keys.json found. Skipping initialization."
        read -p "Unseal Vault? (y/n): " -n 1 -r
        echo
        if [[ $REPLY =~ ^[Yy]$ ]]; then
            unseal_vault
        fi
        
        read -p "Configure Vault? (y/n): " -n 1 -r
        echo
        if [[ $REPLY =~ ^[Yy]$ ]]; then
            configure_vault
        fi
        
        read -p "Populate secrets? (y/n): " -n 1 -r
        echo
        if [[ $REPLY =~ ^[Yy]$ ]]; then
            populate_secrets
        fi
    fi
    
    echo
    print_success "Vault setup complete!"
    echo
    print_info "Next steps:"
    echo "  1. Backup vault-keys.json.gpg to secure offline storage"
    echo "  2. Store VAULT_ROLE_ID and VAULT_SECRET_ID in GitHub Secrets"
    echo "  3. Update microservice deployments with Vault configuration"
    echo "  4. Test secret retrieval from a pod"
    echo
    print_info "To access Vault UI:"
    echo "  kubectl port-forward -n vault svc/vault 8200:8200"
    echo "  Open: http://localhost:8200"
    echo "  Token: $(jq -r '.root_token' vault-keys.json 2>/dev/null || echo 'See vault-keys.json')"
    echo
}

# Run main function
main "$@"
