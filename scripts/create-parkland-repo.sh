#!/bin/bash
###############################################################################
# Create Parkland PCHP Integration Repository
#
# This script packages the PCHP-specific files into a separate repository
# that can be shared with Parkland IT team, keeping Cloud Health Office
# SaaS commercial offering separate.
#
# Usage: ./create-parkland-repo.sh
###############################################################################

set -e

# Colors
BLUE='\033[0;34m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
NC='\033[0m' # No Color

echo -e "${BLUE}=============================================${NC}"
echo -e "${BLUE}Creating Parkland PCHP Integration Repository${NC}"
echo -e "${BLUE}=============================================${NC}"
echo ""

# Source and destination directories
SOURCE_DIR="parkland-repo"
DEST_DIR="../parkland-pchp-integration"

# Check if parkland-repo directory exists
if [ ! -d "$SOURCE_DIR" ]; then
    echo -e "${YELLOW}Error: parkland-repo directory not found${NC}"
    exit 1
fi

# Create destination directory
echo -e "${GREEN}Creating destination directory...${NC}"
mkdir -p "$DEST_DIR"

# Copy files
echo -e "${GREEN}Copying repository files...${NC}"
cp -r "$SOURCE_DIR/"* "$DEST_DIR/"

# Copy configuration file
echo -e "${GREEN}Copying PCHP configuration...${NC}"
cp config/parkland-pchp-config.json "$DEST_DIR/config/"

# Copy Bicep infrastructure template
echo -e "${GREEN}Copying infrastructure template...${NC}"
cp infra/parkland-infrastructure.bicep "$DEST_DIR/infra/main.bicep"

# Create LICENSE file
echo -e "${GREEN}Creating LICENSE file...${NC}"
cat > "$DEST_DIR/LICENSE" << 'EOF'
                                 Apache License
                           Version 2.0, January 2004
                        http://www.apache.org/licenses/

Copyright 2026 Parkland Community Health Plan

Licensed under the Apache License, Version 2.0 (the "License");
you may not use this file except in compliance with the License.
You may obtain a copy of the License at

    http://www.apache.org/licenses/LICENSE-2.0

Unless required by applicable law or agreed to in writing, software
distributed under the License is distributed on an "AS IS" BASIS,
WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
See the License for the specific language governing permissions and
limitations under the License.
EOF

# Create .gitignore
echo -e "${GREEN}Creating .gitignore...${NC}"
cat > "$DEST_DIR/.gitignore" << 'EOF'
# Azure
*.zip
local.settings.json
azuredeploy.json

# Secrets
*.key
*.pem
*.pfx
secrets.json
*.secret

# Build outputs
dist/
node_modules/
bin/
obj/

# IDE
.vscode/
.idea/
*.swp
*.swo

# OS
.DS_Store
Thumbs.db

# Logs
*.log
logs/

# Terraform
.terraform/
*.tfstate
*.tfstate.backup
.terraform.lock.hcl
EOF

# Initialize git repository
echo -e "${GREEN}Initializing git repository...${NC}"
cd "$DEST_DIR"
git init
git add .
git commit -m "Initial commit: PCHP Integration Platform

- Infrastructure templates for Azure deployment
- Member Interoperability API configuration
- File Ingestion Service setup
- QNXT integration configuration
- Cost estimates and deployment documentation
- Architecture diagrams

Organization: Parkland Community Health Plan (PCHP)
Parent Company: Parkland Hospital System"

# Create README for next steps
echo -e "${GREEN}Creating NEXT_STEPS.md...${NC}"
cat > "NEXT_STEPS.md" << 'EOF'
# Next Steps for PCHP Integration Platform

## Repository Setup

1. **Create GitHub Repository**:
   ```bash
   gh repo create parkland-pchp/integration-platform --private
   ```

2. **Push to GitHub**:
   ```bash
   git remote add origin https://github.com/parkland-pchp/integration-platform.git
   git branch -M main
   git push -u origin main
   ```

3. **Update Deploy to Azure Button**:
   Edit README.md and update the URI in the button to point to your repository:
   ```
   https://raw.githubusercontent.com/parkland-pchp/integration-platform/main/azuredeploy.json
   ```

## Before Deployment

1. **Update Configuration**:
   - Edit `config/parkland-pchp-config.json`
   - Set actual Okta domain
   - Configure QNXT endpoint URLs
   - Update contact information

2. **Update Parameters**:
   - Edit `azuredeploy.parameters.json`
   - Set Hub VNet resource ID from Parkland Hospital System
   - Choose appropriate environment (dev/uat/prod)

3. **Compile Bicep to ARM**:
   ```bash
   az bicep build --file infra/main.bicep --outfile azuredeploy.json
   ```

4. **Validate Template**:
   ```bash
   az deployment group validate \
     --resource-group pchp-integration-rg \
     --template-file azuredeploy.json \
     --parameters @azuredeploy.parameters.json
   ```

## Deployment

Follow the instructions in `docs/DEPLOYMENT-GUIDE.md` for detailed deployment steps.

## Sharing with Parkland IT

This repository is ready to share with Parkland Hospital System IT team. It contains:

- ✅ Complete infrastructure templates
- ✅ Detailed cost estimates (~$1,725/month dev, ~$3,200/month prod)
- ✅ Architecture diagrams
- ✅ Deployment documentation
- ✅ "Deploy to Azure" button for one-click deployment

The repository is separate from Cloud Health Office SaaS commercial offering.
EOF

cd -

echo ""
echo -e "${BLUE}=============================================${NC}"
echo -e "${GREEN}✓ Parkland PCHP Repository Created!${NC}"
echo -e "${BLUE}=============================================${NC}"
echo ""
echo -e "Location: ${YELLOW}$DEST_DIR${NC}"
echo ""
echo -e "Next steps:"
echo -e "1. cd $DEST_DIR"
echo -e "2. Review NEXT_STEPS.md"
echo -e "3. Create GitHub repository and push"
echo -e "4. Share with Parkland IT team"
echo ""
echo -e "${GREEN}The repository is ready to share with Parkland IT!${NC}"
echo ""
