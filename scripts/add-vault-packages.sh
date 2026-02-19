#!/usr/bin/env bash
# add-vault-packages.sh - Add VaultSharp NuGet packages to all microservices

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SERVICES_DIR="$SCRIPT_DIR/../src/services"

# Colors
GREEN='\033[0;32m'
BLUE='\033[0;34m'
NC='\033[0m'

echo "================================================"
echo "  Adding VaultSharp Packages to Microservices"
echo "================================================"
echo

# List of all microservices
SERVICES=(
    "member-service"
    "claims-service"
    "eligibility-service"
    "authorization-service"
    "coverage-service"
    "provider-service"
    "tenant-service"
    "appeals-service"
    "attachment-service"
    "benefit-plan-service"
    "claims-scrubbing-service"
    "enrollment-import-service"
    "payment-service"
    "reference-data-service"
    "sponsor-service"
    "trading-partner-service"
)

# VaultSharp package version
VAULTSHARP_VERSION="1.13.0.1"

# Counter for successful additions
SUCCESS_COUNT=0
TOTAL_COUNT=0

for service in "${SERVICES[@]}"; do
    SERVICE_PATH="$SERVICES_DIR/$service"
    CSPROJ_FILE="$SERVICE_PATH/${service}.csproj"
    
    # Handle special cases with different csproj names
    if [ ! -f "$CSPROJ_FILE" ]; then
        # Try PascalCase conversion
        PASCAL_CASE=$(echo "$service" | awk 'BEGIN{FS="-"; OFS=""} {for(i=1;i<=NF;i++) $i=toupper(substr($i,1,1)) substr($i,2)} 1')
        CSPROJ_FILE="$SERVICE_PATH/${PASCAL_CASE}.csproj"
    fi
    
    if [ ! -f "$CSPROJ_FILE" ]; then
        echo -e "${BLUE}⚠  Skipping $service - .csproj file not found${NC}"
        continue
    fi
    
    TOTAL_COUNT=$((TOTAL_COUNT + 1))
    
    echo -e "${BLUE}Processing: $service${NC}"
    
    # Check if VaultSharp is already referenced
    if grep -q "VaultSharp" "$CSPROJ_FILE"; then
        echo "  ✓ VaultSharp already referenced"
        SUCCESS_COUNT=$((SUCCESS_COUNT + 1))
        continue
    fi
    
    # Add VaultSharp package reference
    # Find the last PackageReference line and add after it
    if grep -q "<PackageReference" "$CSPROJ_FILE"; then
        # Use sed to add VaultSharp reference after the last PackageReference
        sed -i '/<\/ItemGroup>/i \    <PackageReference Include="VaultSharp" Version="'"$VAULTSHARP_VERSION"'" />\n    <PackageReference Include="VaultSharp.Extensions.Configuration" Version="'"$VAULTSHARP_VERSION"'" />' "$CSPROJ_FILE"
        
        echo -e "  ${GREEN}✓ Added VaultSharp $VAULTSHARP_VERSION${NC}"
        SUCCESS_COUNT=$((SUCCESS_COUNT + 1))
    else
        echo "  ⚠  No PackageReference section found - manual addition required"
    fi
done

echo
echo "================================================"
echo "  Summary"
echo "================================================"
echo "  Total services processed: $TOTAL_COUNT"
echo "  Successfully updated: $SUCCESS_COUNT"
echo
echo "Next steps:"
echo "  1. Review changes with: git diff src/services/"
echo "  2. Restore packages: dotnet restore"
echo "  3. Build to verify: dotnet build"
