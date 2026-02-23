#!/bin/bash
#
# CloudHealthOffice Whitepaper PDF Generator
#
# Converts WHITEPAPER-CMS-0057-F-COMPLIANCE.md to PDF using pandoc
# with weasyprint for high-quality PDF output.
#
# Prerequisites:
#   - pandoc: https://pandoc.org/installing.html
#   - weasyprint: pip install weasyprint
#   - mermaid-filter (optional): npm install -g mermaid-filter
#
# Usage:
#   ./scripts/generate-whitepaper-pdf.sh
#   npm run generate-pdf
#

set -e

# Colors for output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
CYAN='\033[0;36m'
NC='\033[0m' # No Color

# File paths
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT_DIR="$(dirname "$SCRIPT_DIR")"
DOCS_DIR="$ROOT_DIR/docs"
INPUT_FILE="$DOCS_DIR/WHITEPAPER-CMS-0057-F-COMPLIANCE.md"
OUTPUT_FILE="$DOCS_DIR/WHITEPAPER-CMS-0057-F-COMPLIANCE.pdf"
CSS_FILE="$DOCS_DIR/whitepaper-style.css"

echo "═══════════════════════════════════════════════════════════════"
echo "  CloudHealthOffice Whitepaper PDF Generator"
echo "═══════════════════════════════════════════════════════════════"
echo ""

# Check prerequisites
echo -e "${CYAN}🔍 Checking prerequisites...${NC}"
echo ""

# Check pandoc
if ! command -v pandoc &> /dev/null; then
    echo -e "${RED}❌ pandoc is not installed.${NC}"
    echo "   Install from: https://pandoc.org/installing.html"
    echo "   - macOS: brew install pandoc"
    echo "   - Ubuntu: apt-get install pandoc"
    echo "   - Windows: choco install pandoc"
    exit 1
fi
echo -e "${GREEN}✅ pandoc found${NC}"

# Check weasyprint
if ! command -v weasyprint &> /dev/null; then
    echo -e "${RED}❌ weasyprint is not installed.${NC}"
    echo "   Install with: pip install weasyprint"
    exit 1
fi
echo -e "${GREEN}✅ weasyprint found${NC}"

# Check mermaid-filter (optional)
MERMAID_FILTER=""
if command -v mermaid-filter &> /dev/null; then
    echo -e "${GREEN}✅ mermaid-filter found (Mermaid diagrams will be rendered)${NC}"
    MERMAID_FILTER="--filter mermaid-filter"
else
    echo -e "${YELLOW}⚠️  mermaid-filter not found (Mermaid code blocks will be shown as-is)${NC}"
    echo "   Optional install: npm install -g mermaid-filter"
fi

echo ""

# Check input file
if [ ! -f "$INPUT_FILE" ]; then
    echo -e "${RED}❌ Input file not found: $INPUT_FILE${NC}"
    exit 1
fi

# Check CSS file
if [ ! -f "$CSS_FILE" ]; then
    echo -e "${RED}❌ CSS file not found: $CSS_FILE${NC}"
    echo "   Ensure docs/whitepaper-style.css exists before running."
    exit 1
fi

# Generate PDF
echo -e "${CYAN}📄 Generating PDF...${NC}"
echo ""
echo "Running pandoc conversion..."
echo ""

# Build pandoc command
PANDOC_CMD="pandoc \"$INPUT_FILE\" \
    -o \"$OUTPUT_FILE\" \
    --from markdown+footnotes+pipe_tables+strikeout \
    --to pdf \
    --pdf-engine=weasyprint \
    --css=\"$CSS_FILE\" \
    --metadata title=\"CloudHealthOffice CMS-0057-F Compliance Whitepaper\" \
    --metadata author=\"Aurelianware\" \
    --metadata date=\"November 2025\" \
    --toc \
    --toc-depth=3 \
    --standalone \
    --highlight-style=tango \
    $MERMAID_FILTER"

# Execute pandoc
if eval $PANDOC_CMD 2>/dev/null; then
    echo -e "${GREEN}✅ PDF generated successfully: $OUTPUT_FILE${NC}"
else
    echo -e "${RED}❌ PDF generation failed${NC}"
    echo ""
    echo -e "${YELLOW}🔄 Trying alternative PDF engine (pdflatex)...${NC}"
    
    # Try alternative without weasyprint
    ALT_CMD="pandoc \"$INPUT_FILE\" \
        -o \"$OUTPUT_FILE\" \
        --from markdown+footnotes+pipe_tables+strikeout \
        --to pdf \
        --pdf-engine=pdflatex \
        --metadata title=\"CloudHealthOffice CMS-0057-F Compliance Whitepaper\" \
        --metadata author=\"Aurelianware\" \
        --metadata date=\"November 2025\" \
        --toc \
        --toc-depth=3 \
        --standalone \
        --highlight-style=tango \
        $MERMAID_FILTER"
    
    if eval $ALT_CMD 2>/dev/null; then
        echo -e "${GREEN}✅ PDF generated with pdflatex: $OUTPUT_FILE${NC}"
    else
        echo -e "${RED}❌ Alternative PDF generation also failed.${NC}"
        echo "   Please ensure pandoc and weasyprint are properly installed."
        exit 1
    fi
fi

echo ""
echo "═══════════════════════════════════════════════════════════════"
echo -e "  ${GREEN}✅ Whitepaper PDF generation complete!${NC}"
echo "  📄 Output: $OUTPUT_FILE"
echo "═══════════════════════════════════════════════════════════════"
echo ""
