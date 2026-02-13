#!/bin/bash
# Parse 837 EDI files to JSON
# Supports 837P (Professional), 837I (Institutional), 837D (Dental)

set -e

INPUT_DIR=${INPUT_DIR:-"/work/input"}
OUTPUT_DIR=${OUTPUT_DIR:-"/work/output"}
CLAIM_TYPES=${CLAIM_TYPES:-"837P,837I,837D"}

echo "Starting 837 EDI parser..."
echo "Input directory: $INPUT_DIR"
echo "Output directory: $OUTPUT_DIR"
echo "Claim types: $CLAIM_TYPES"

# Create output directory
mkdir -p "$OUTPUT_DIR"

# Count input files
FILE_COUNT=$(ls -1 "$INPUT_DIR"/*.edi 2>/dev/null | wc -l)
echo "Found $FILE_COUNT EDI files"

if [ "$FILE_COUNT" -eq 0 ]; then
    echo "No EDI files to process"
    exit 0
fi

# Process each EDI file
PARSED_COUNT=0
ERROR_COUNT=0

for edi_file in "$INPUT_DIR"/*.edi; do
    filename=$(basename "$edi_file" .edi)
    output_file="$OUTPUT_DIR/${filename}.json"
    
    echo "Processing: $edi_file"
    
    # Parse EDI to JSON using Node.js x12-parser
    if node /app/parse-837.js "$edi_file" "$output_file"; then
        echo "  ✓ Parsed successfully: $output_file"
        ((PARSED_COUNT++))
    else
        echo "  ✗ Parse failed: $edi_file"
        ((ERROR_COUNT++))
    fi
done

echo "Parsing complete:"
echo "  Parsed: $PARSED_COUNT"
echo "  Errors: $ERROR_COUNT"

# Write summary
cat > "$OUTPUT_DIR/parse-summary.json" <<EOF
{
  "totalFiles": $FILE_COUNT,
  "parsedCount": $PARSED_COUNT,
  "errorCount": $ERROR_COUNT,
  "timestamp": "$(date -u +%Y-%m-%dT%H:%M:%SZ)"
}
EOF

if [ "$ERROR_COUNT" -gt 0 ]; then
    echo "Warning: $ERROR_COUNT files failed to parse"
    exit 0  # Don't fail workflow, just log errors
fi

echo "All files parsed successfully"
