// Parse 837 EDI file to JSON
// Usage: node parse-837.js input.edi output.json

const fs = require('fs');
const X12Parser = require('@hahntech/x12-parser');

const inputFile = process.argv[2];
const outputFile = process.argv[3];

if (!inputFile || !outputFile) {
  console.error('Usage: node parse-837.js <input.edi> <output.json>');
  process.exit(1);
}

// Read EDI file
const ediContent = fs.readFileSync(inputFile, 'utf8');

try {
  // Parse EDI
  const parser = new X12Parser();
  const result = parser.parse(ediContent);
  
  // Extract claim data from parsed result
  const claim = extract837Claim(result);
  
  // Write JSON output
  fs.writeFileSync(outputFile, JSON.stringify(claim, null, 2));
  
  console.log(`✓ Parsed ${inputFile} → ${outputFile}`);
  process.exit(0);
} catch (error) {
  console.error(`✗ Parse error: ${error.message}`);
  process.exit(1);
}

function extract837Claim(parsed) {
  // Extract claim details from parsed X12 structure
  // This is simplified - real implementation would extract all segments
  
  const claim = {
    claimNumber: extractValue(parsed, 'CLM', '01'),
    totalCharge: parseFloat(extractValue(parsed, 'CLM', '02') || '0'),
    memberId: extractValue(parsed, 'NM1', '09', { qualifier: 'IL' }),
    providerNPI: extractValue(parsed, 'NM1', '09', { qualifier: 'PR' }),
    serviceDate: extractValue(parsed, 'DTP', '03', { qualifier: '472' }),
    diagnosisCodes: extractDiagnosisCodes(parsed),
    claimLines: extractClaimLines(parsed),
    claimType: detectClaimType(parsed),
    status: 'Submitted',
    submittedDate: new Date().toISOString()
  };
  
  return claim;
}

function extractValue(parsed, segment, element, qualifier = null) {
  // Simplified extraction logic
  // Real implementation would traverse parsed structure properly
  return '';
}

function extractDiagnosisCodes(parsed) {
  // Extract HI segments for diagnosis codes
  return [];
}

function extractClaimLines(parsed) {
  // Extract LX/SV1/SV2 segments for claim lines
  return [];
}

function detectClaimType(parsed) {
  // Detect 837P, 837I, or 837D from ST segment
  const st = parsed?.segments?.find(s => s.tag === 'ST');
  if (st?.elements[0] === '837' && st?.elements[2]?.includes('X222')) return 'Professional';
  if (st?.elements[0] === '837' && st?.elements[2]?.includes('X223')) return 'Institutional';
  if (st?.elements[0] === '837' && st?.elements[2]?.includes('X224')) return 'Dental';
  return 'Professional';
}
