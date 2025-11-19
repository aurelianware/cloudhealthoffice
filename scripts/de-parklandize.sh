#!/bin/bash
##############################################################################
# De-Parklandization Script for HIPAA Attachments Repository
# 
# Purpose: Remove all hardcoded Parkland Community Health Plan (PCHP) 
#          references and replace with generic health plan placeholders
#          to enable multi-payer platformization.
#
# This script processes all relevant files in the repository and replaces:
#   - "Parkland Community Health Plan" → "Health Plan"
#   - "PCHP" → "Health Plan"
#   - "Parkland" → "Health Plan"
#   - "66917" → "{config.payerId}"
#   - "pchp_" → "payer_"
#   - "PCHP_" → "PAYER_"
#
# Usage: ./scripts/de-parklandize.sh
#
# The script will:
#   1. Search for all Parkland/PCHP references
#   2. Replace them with generic placeholders
#   3. Generate a summary report
#   4. Provide next steps guidance
#
##############################################################################

set -e  # Exit on error

# Color codes for output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
BLUE='\033[0;34m'
NC='\033[0m' # No Color

# Counters
TOTAL_CHANGES=0
FILES_MODIFIED=0

# Repository root (one level up from scripts directory)
REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"

echo -e "${BLUE}===============================================${NC}"
echo -e "${BLUE}De-Parklandization Script${NC}"
echo -e "${BLUE}===============================================${NC}"
echo ""
echo -e "Repository root: ${YELLOW}${REPO_ROOT}${NC}"
echo ""

# Function to perform safe sed replacement (macOS and Linux compatible)
safe_sed() {
    local pattern="$1"
    local replacement="$2"
    local file="$3"
    
    if [[ "$OSTYPE" == "darwin"* ]]; then
        # macOS requires empty string after -i
        sed -i '' "s/${pattern}/${replacement}/g" "$file"
    else
        # Linux
        sed -i "s/${pattern}/${replacement}/g" "$file"
    fi
}

# Function to count occurrences before replacement
count_occurrences() {
    local pattern="$1"
    local file="$2"
    grep -o "$pattern" "$file" 2>/dev/null | wc -l | tr -d ' '
}

# Function to process a file with all replacements
process_file() {
    local file="$1"
    local file_changes=0
    local before_count=0
    local after_count=0
    
    # Skip this script itself
    if [[ "$file" == *"de-parklandize.sh"* ]]; then
        return 0
    fi
    
    # Count total Parkland references before processing
    before_count=$(grep -i "parkland\|pchp\|66917" "$file" 2>/dev/null | wc -l | tr -d ' ')
    
    if [ "$before_count" -gt 0 ]; then
        echo -e "${YELLOW}Processing: ${file}${NC}"
        
        # Replacement 1: "Parkland Community Health Plan" → "Health Plan"
        safe_sed "Parkland Community Health Plan" "Health Plan" "$file"
        
        # Replacement 2: "PCHP" → "Health Plan" (but not in variable names)
        # This preserves PCHP in contexts like pchp_ variable prefixes
        safe_sed '\bPCHP\b' "Health Plan" "$file"
        
        # Replacement 3: "Parkland" → "Health Plan" (standalone word)
        safe_sed '\bParkland\b' "Health Plan" "$file"
        
        # Replacement 4: "66917" → "{config.payerId}"
        safe_sed "66917" "{config.payerId}" "$file"
        
        # Replacement 5: "pchp_" → "payer_" (variable prefixes)
        safe_sed "pchp_" "payer_" "$file"
        
        # Replacement 6: "PCHP_" → "PAYER_" (constant prefixes)
        safe_sed "PCHP_" "PAYER_" "$file"
        
        # Replacement 7: "PCHP-" → "PAYER-" (in identifiers)
        safe_sed "PCHP-" "PAYER-" "$file"
        
        # Replacement 8: "pchp-" → "payer-" (in identifiers)
        safe_sed "pchp-" "payer-" "$file"
        
        # Count after processing
        after_count=$(grep -i "parkland\|pchp\|66917" "$file" 2>/dev/null | wc -l | tr -d ' ')
        
        file_changes=$((before_count - after_count))
        TOTAL_CHANGES=$((TOTAL_CHANGES + file_changes))
        FILES_MODIFIED=$((FILES_MODIFIED + 1))
        
        echo -e "  ${GREEN}✓ Replaced ${file_changes} reference(s)${NC}"
    fi
}

echo -e "${BLUE}Step 1: Discovering files...${NC}"
echo ""

# Find all relevant files (excluding .git, node_modules, and binary files)
FILE_PATTERNS=(
    "*.md"
    "*.json"
    "*.yaml"
    "*.yml"
    "*.bicep"
    "*.ps1"
    "*.sh"
)

FILES_TO_PROCESS=()

for pattern in "${FILE_PATTERNS[@]}"; do
    while IFS= read -r -d '' file; do
        FILES_TO_PROCESS+=("$file")
    done < <(find "$REPO_ROOT" -type f -name "$pattern" ! -path "*/.git/*" ! -path "*/node_modules/*" -print0)
done

echo -e "Found ${YELLOW}${#FILES_TO_PROCESS[@]}${NC} files to process"
echo ""

echo -e "${BLUE}Step 2: Processing files...${NC}"
echo ""

# Process each file
for file in "${FILES_TO_PROCESS[@]}"; do
    process_file "$file"
done

echo ""
echo -e "${BLUE}===============================================${NC}"
echo -e "${GREEN}De-Parklandization Complete!${NC}"
echo -e "${BLUE}===============================================${NC}"
echo ""
echo -e "Summary:"
echo -e "  Files modified: ${GREEN}${FILES_MODIFIED}${NC}"
echo -e "  Total changes: ${GREEN}${TOTAL_CHANGES}${NC}"
echo ""

echo -e "${BLUE}Step 3: Verification${NC}"
echo ""
echo "Checking for remaining Parkland/PCHP references (excluding this script)..."
echo ""

REMAINING=$(grep -r "Parkland\|PCHP\|66917" "$REPO_ROOT" \
    --exclude-dir=".git" \
    --exclude-dir="node_modules" \
    --exclude="de-parklandize.sh" \
    --include="*.md" \
    --include="*.json" \
    --include="*.yaml" \
    --include="*.yml" \
    --include="*.bicep" \
    --include="*.ps1" \
    --include="*.sh" \
    2>/dev/null || true)

if [ -z "$REMAINING" ]; then
    echo -e "${GREEN}✓ No remaining Parkland/PCHP references found!${NC}"
else
    echo -e "${YELLOW}⚠ Found remaining references:${NC}"
    echo "$REMAINING"
    echo ""
    echo -e "${YELLOW}Note: Some references may be intentional (e.g., in test data or examples).${NC}"
fi

echo ""
echo -e "${BLUE}===============================================${NC}"
echo -e "${BLUE}Next Steps${NC}"
echo -e "${BLUE}===============================================${NC}"
echo ""
echo "1. Review the changes:"
echo "   git diff"
echo ""
echo "2. Validate JSON files:"
echo "   find . -name \"*.json\" -type f | xargs -I {} jq empty {}"
echo ""
echo "3. Compile Bicep files:"
echo "   az bicep build --file infra/main.bicep"
echo ""
echo "4. Test workflows:"
echo "   # Deploy to DEV environment and test"
echo ""
echo "5. Commit changes:"
echo "   git add ."
echo "   git commit -m \"De-Parklandize: Remove hardcoded PCHP references for multi-payer support\""
echo ""
echo -e "${GREEN}De-parklandization script completed successfully!${NC}"
echo ""

exit 0
