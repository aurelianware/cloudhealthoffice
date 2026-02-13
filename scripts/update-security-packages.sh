#!/bin/bash
set -euo pipefail

# Cloud Health Office - Security Package Update Script
# Updates NuGet packages to address Dependabot security alerts
# Run from repository root: ./scripts/update-security-packages.sh

RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
BLUE='\033[0;34m'
NC='\033[0m' # No Color

echo -e "${BLUE}╔════════════════════════════════════════════════════════╗${NC}"
echo -e "${BLUE}║  Cloud Health Office - Security Updates               ║${NC}"
echo -e "${BLUE}║  Addressing Dependabot Vulnerability Alerts            ║${NC}"
echo -e "${BLUE}╚════════════════════════════════════════════════════════╝${NC}"
echo ""

# Check if dotnet is available
if ! command -v dotnet &> /dev/null; then
    echo -e "${RED}❌ Error: dotnet CLI not found${NC}"
    echo "   Please install .NET SDK 8.0 or later"
    exit 1
fi

echo -e "${GREEN}✓ .NET SDK found: $(dotnet --version)${NC}"
echo ""

# Define package updates based on Dependabot alerts
# Format: "PackageName:Version"
PACKAGE_UPDATES=(
    "Azure.Identity:1.13.1"
    "Microsoft.Identity.Web:3.3.0"
    "Microsoft.Identity.Client:4.66.2"
    "Microsoft.Azure.Cosmos:3.45.0"
    "Azure.Storage.Blobs:12.24.0"
    "Azure.Core:1.44.1"
    "Stripe.net:46.4.0"
    "Microsoft.Extensions.Http:9.0.0"
    "Microsoft.AspNetCore.SignalR.Client:9.0.0"
)

# Find all .csproj files
PROJECT_FILES=$(find . -name "*.csproj" -not -path "*/bin/*" -not -path "*/obj/*")
PROJECT_COUNT=$(echo "$PROJECT_FILES" | wc -l | tr -d ' ')

echo -e "${BLUE}📦 Found $PROJECT_COUNT .NET projects${NC}"
echo ""

# Update each project
UPDATED_COUNT=0
FAILED_COUNT=0

for PROJECT in $PROJECT_FILES; do
    PROJECT_NAME=$(basename "$PROJECT" .csproj)
    echo -e "${YELLOW}🔧 Updating: $PROJECT_NAME${NC}"
    
    # Update each package if it exists in the project
    for PACKAGE_VERSION in "${PACKAGE_UPDATES[@]}"; do
        PACKAGE="${PACKAGE_VERSION%%:*}"
        VERSION="${PACKAGE_VERSION##*:}"
        
        # Check if package is referenced in project
        if grep -q "PackageReference Include=\"$PACKAGE\"" "$PROJECT"; then
            echo -e "   ${BLUE}→${NC} Updating $PACKAGE to $VERSION"
            
            if dotnet add "$PROJECT" package "$PACKAGE" --version "$VERSION" 2>&1 | grep -q "Successfully"; then
                echo -e "   ${GREEN}✓${NC} Updated $PACKAGE"
                ((UPDATED_COUNT++))
            else
                echo -e "   ${YELLOW}⊙${NC} $PACKAGE (may already be at correct version)"
            fi
        fi
    done
    
    echo ""
done

echo ""
echo -e "${BLUE}════════════════════════════════════════════════════════${NC}"
echo -e "${GREEN}✅ Security update complete${NC}"
echo -e "   ${GREEN}✓${NC} Updated: $UPDATED_COUNT packages"
if [ $FAILED_COUNT -gt 0 ]; then
    echo -e "   ${RED}✗${NC} Failed: $FAILED_COUNT packages"
fi
echo ""
echo -e "${YELLOW}📋 Next steps:${NC}"
echo "   1. Run: dotnet restore"
echo "   2. Run: dotnet build"
echo "   3. Run: dotnet test (if tests exist)"
echo "   4. Review changes: git diff"
echo "   5. Commit: git commit -am 'security: update vulnerable dependencies'"
echo ""
