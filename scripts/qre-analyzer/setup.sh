#!/bin/bash
#
# X12 278 X215 QRE Analyzer Setup Script
#
# This script sets up the QRE analyzer environment and verifies installation.
#

set -e  # Exit on error

echo "========================================="
echo "X12 278 X215 QRE Analyzer Setup"
echo "========================================="
echo

# Check Python version
echo "Checking Python version..."
if command -v python3 &> /dev/null; then
    PYTHON_VERSION=$(python3 --version 2>&1 | awk '{print $2}')
    echo "✓ Found Python $PYTHON_VERSION"
    
    # Check if version is 3.7 or higher
    MAJOR=$(echo $PYTHON_VERSION | cut -d. -f1)
    MINOR=$(echo $PYTHON_VERSION | cut -d. -f2)
    
    if [ "$MAJOR" -lt 3 ] || ([ "$MAJOR" -eq 3 ] && [ "$MINOR" -lt 7 ]); then
        echo "✗ Error: Python 3.7 or higher is required (found $PYTHON_VERSION)"
        exit 1
    fi
else
    echo "✗ Error: Python 3 is not installed"
    echo "  Install Python 3.7+ and try again"
    exit 1
fi

echo

# Make analyzer executable
echo "Making analyzer executable..."
chmod +x x12_278_qre_analyzer.py
echo "✓ x12_278_qre_analyzer.py is now executable"

echo

# Verify configuration files exist
echo "Verifying configuration files..."

if [ -f "qre-analyzer-config.schema.json" ]; then
    echo "✓ Found qre-analyzer-config.schema.json"
else
    echo "✗ Warning: qre-analyzer-config.schema.json not found"
fi

if [ -f "qre-analyzer.config.json" ]; then
    echo "✓ Found qre-analyzer.config.json"
    
    # Validate JSON syntax
    if python3 -m json.tool qre-analyzer.config.json > /dev/null 2>&1; then
        echo "✓ Configuration JSON is valid"
    else
        echo "✗ Warning: Configuration JSON has syntax errors"
    fi
else
    echo "✗ Warning: qre-analyzer.config.json not found"
fi

echo

# Test the analyzer with a simple invocation
echo "Testing analyzer..."
if python3 x12_278_qre_analyzer.py > /dev/null 2>&1; then
    echo "✗ Unexpected: analyzer should require a file argument"
else
    # Check if it printed usage
    if python3 x12_278_qre_analyzer.py 2>&1 | grep -q "Usage:"; then
        echo "✓ Analyzer responds correctly to usage check"
    else
        echo "✗ Warning: Analyzer may have issues"
    fi
fi

echo

# Check for example files
echo "Checking for example X12 278 files..."
EXAMPLE_DIR="../../docs/examples/authorizations/inquiry"

if [ -d "$EXAMPLE_DIR" ]; then
    EDI_FILES=$(find "$EXAMPLE_DIR" -name "*.edi" 2>/dev/null | wc -l)
    if [ "$EDI_FILES" -gt 0 ]; then
        echo "✓ Found $EDI_FILES example EDI file(s)"
        echo
        echo "  You can test the analyzer with:"
        find "$EXAMPLE_DIR" -name "*.edi" | head -3 | while read file; do
            echo "    python3 x12_278_qre_analyzer.py $file"
        done
    else
        echo "  No example EDI files found in $EXAMPLE_DIR"
    fi
else
    echo "  Example directory not found: $EXAMPLE_DIR"
fi

echo
echo "========================================="
echo "Setup Complete!"
echo "========================================="
echo
echo "Usage:"
echo "  python3 x12_278_qre_analyzer.py <file.edi>"
echo
echo "Example:"
echo "  python3 x12_278_qre_analyzer.py ../../docs/examples/authorizations/inquiry/x215-inquiry-by-auth-number-request.edi"
echo
echo "For more information, see README.md"
echo

exit 0
