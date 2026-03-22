#!/bin/bash
# Quick setup script for DNS and IP whitelisting
# Cloud Health Office - SFTP Production Hardening

set -euo pipefail

echo "=========================================="
echo "SFTP DNS & IP Whitelisting Setup"
echo "=========================================="
echo ""

# Check prerequisites
if ! command -v kubectl &> /dev/null; then
    echo "❌ kubectl not found"
    exit 1
fi

if ! command -v az &> /dev/null; then
    echo "❌ Azure CLI not found"
    exit 1
fi

# Get SFTP LoadBalancer IP
echo "📡 Getting SFTP LoadBalancer IP..."
SFTP_IP=$(kubectl get svc sftp-service -n cho-sftp -o jsonpath='{.status.loadBalancer.ingress[0].ip}' 2>/dev/null || echo "")

if [ -z "$SFTP_IP" ]; then
    echo "❌ SFTP LoadBalancer IP not found. Deploy SFTP first:"
    echo "   ./scripts/deploy-sftp-server.sh"
    exit 1
fi

echo "✅ SFTP IP: $SFTP_IP"
echo ""

# Step 1: DNS Configuration
echo "=========================================="
echo "Step 1: DNS Configuration"
echo "=========================================="
echo ""

read -p "Configure DNS? (y/n) " -n 1 -r
echo
if [[ $REPLY =~ ^[Yy]$ ]]; then
    read -p "Domain name (e.g., cloudhealthoffice.com): " DOMAIN_NAME
    read -p "Subdomain (e.g., sftp): " SUBDOMAIN
    read -p "Azure Resource Group for DNS zone: " DNS_RG
    
    FQDN="${SUBDOMAIN}.${DOMAIN_NAME}"
    
    echo ""
    echo "Creating DNS zone and A record..."
    
    # Create DNS zone (ignore if exists)
    az network dns zone create \
      --resource-group "$DNS_RG" \
      --name "$DOMAIN_NAME" 2>/dev/null || echo "DNS zone already exists"
    
    # Create/update A record
    az network dns record-set a create \
      --resource-group "$DNS_RG" \
      --zone-name "$DOMAIN_NAME" \
      --name "$SUBDOMAIN" 2>/dev/null || echo "A record already exists"
    
    az network dns record-set a add-record \
      --resource-group "$DNS_RG" \
      --zone-name "$DOMAIN_NAME" \
      --record-set-name "$SUBDOMAIN" \
      --ipv4-address "$SFTP_IP"
    
    echo ""
    echo "✅ DNS record created: $FQDN → $SFTP_IP"
    echo ""
    echo "📋 Nameservers (configure at your registrar):"
    az network dns zone show \
      --resource-group "$DNS_RG" \
      --name "$DOMAIN_NAME" \
      --query nameServers -o tsv
    
    echo ""
    echo "⏳ Wait 5-60 minutes for DNS propagation, then test:"
    echo "   dig $FQDN +short"
    echo "   sftp cho-edi@$FQDN"
else
    FQDN="$SFTP_IP"
    echo "Skipping DNS configuration. Using IP: $SFTP_IP"
fi

echo ""

# Step 2: IP Whitelisting
echo "=========================================="
echo "Step 2: IP Whitelisting"
echo "=========================================="
echo ""

read -p "Configure IP whitelisting? (y/n) " -n 1 -r
echo
if [[ $REPLY =~ ^[Yy]$ ]]; then
    echo ""
    echo "Collecting IP addresses..."
    echo ""
    
    echo "📍 Clearinghouse IPs:"
    echo "Enter clearinghouse IP ranges (CIDR notation, one per line, empty line to finish):"
    
    CLEARINGHOUSE_IPS=()
    while true; do
        read -p "IP/CIDR: " ip
        [ -z "$ip" ] && break
        CLEARINGHOUSE_IPS+=("$ip")
    done
    
    echo ""
    read -p "Add your current IP for testing? (y/n) " -n 1 -r
    echo
    ADMIN_IPS=()
    if [[ $REPLY =~ ^[Yy]$ ]]; then
        MY_IP=$(curl -s -4 ifconfig.me)
        echo "Your IP: $MY_IP"
        ADMIN_IPS+=("$MY_IP/32")
    fi
    
    # Build loadBalancerSourceRanges YAML
    echo ""
    echo "Generating IP whitelist configuration..."
    
    WHITELIST_YAML="  loadBalancerSourceRanges:\n"
    
    # Add Clearinghouse IPs
    for ip in "${CLEARINGHOUSE_IPS[@]}"; do
        WHITELIST_YAML+="    - $ip\n"
    done
    
    # Add Admin IPs
    for ip in "${ADMIN_IPS[@]}"; do
        WHITELIST_YAML+="    - $ip  # Admin - remove for production\n"
    done
    
    echo ""
    echo "IP Whitelist Configuration:"
    echo -e "$WHITELIST_YAML"
    echo ""
    
    read -p "Apply IP whitelist to LoadBalancer? (y/n) " -n 1 -r
    echo
    if [[ $REPLY =~ ^[Yy]$ ]]; then
        # Build JSON patch
        RANGES_JSON="["
        first=true
        for ip in "${CLEARINGHOUSE_IPS[@]}"; do
            [ "$first" = false ] && RANGES_JSON+=","
            RANGES_JSON+="\"$ip\""
            first=false
        done
        for ip in "${ADMIN_IPS[@]}"; do
            [ "$first" = false ] && RANGES_JSON+=","
            RANGES_JSON+="\"$ip\""
            first=false
        done
        RANGES_JSON+="]"
        
        kubectl patch svc sftp-service -n cho-sftp -p "{
          \"spec\": {
            \"loadBalancerSourceRanges\": $RANGES_JSON
          }
        }"
        
        echo ""
        echo "✅ IP whitelist applied!"
        echo ""
        echo "⚠️  Note: Changes may take 1-2 minutes to propagate"
        echo ""
        echo "Test connection:"
        if [ "$FQDN" != "$SFTP_IP" ]; then
            echo "  sftp cho-edi@$FQDN"
        else
            echo "  sftp cho-edi@$SFTP_IP"
        fi
    fi
else
    echo "Skipping IP whitelisting."
fi

echo ""
echo "=========================================="
echo "✅ Setup Complete!"
echo "=========================================="
echo ""

if [ "$FQDN" != "$SFTP_IP" ]; then
    echo "SFTP Endpoint: $FQDN"
else
    echo "SFTP Endpoint: $SFTP_IP"
fi

echo ""
echo "📋 Next Steps:"
echo "1. Wait for DNS propagation (if configured)"
echo "2. Test SFTP connection from allowed IP"
echo "3. Run: ./scripts/setup/configure-sftp-connection.sh"
echo ""
echo "📖 Full documentation: docs/SFTP-DNS-SETUP.md"
echo ""
