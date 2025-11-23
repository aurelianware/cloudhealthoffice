/**
 * Generate Synthetic 837 EDI Claims for Testing
 * Creates HIPAA-compliant but fake 837P (Professional) and 837I (Institutional) claims
 * All data is synthetic and PHI-safe for testing purposes
 */

import * as fs from 'fs';
import * as path from 'path';

// Synthetic test data - not real PHI
const SYNTHETIC_PROVIDERS = [
  { npi: '1234567893', name: 'SMITH MEDICAL GROUP', taxId: '123456789' },
  { npi: '9876543219', name: 'JONES FAMILY PRACTICE', taxId: '987654321' },
  { npi: '5555555559', name: 'COMMUNITY HEALTH CENTER', taxId: '555555555' },
];

const SYNTHETIC_PATIENTS = [
  { firstName: 'JOHN', lastName: 'DOE', dob: '19800115', memberId: 'TEST000001' },
  { firstName: 'JANE', lastName: 'SMITH', dob: '19751203', memberId: 'TEST000002' },
  { firstName: 'ROBERT', lastName: 'JOHNSON', dob: '19900522', memberId: 'TEST000003' },
];

const PROCEDURE_CODES = [
  { code: '99213', description: 'Office Visit Level 3', amount: '150.00' },
  { code: '99214', description: 'Office Visit Level 4', amount: '200.00' },
  { code: '99215', description: 'Office Visit Level 5', amount: '250.00' },
  { code: '85025', description: 'Complete Blood Count', amount: '50.00' },
  { code: '80053', description: 'Comprehensive Metabolic Panel', amount: '75.00' },
];

const DIAGNOSIS_CODES = [
  'E119',   // Type 2 diabetes without complications
  'I10',    // Essential hypertension
  'J069',   // Acute upper respiratory infection
  'M549',   // Dorsalgia
  'R51',    // Headache
];

export interface Claim837Options {
  claimType: '837P' | '837I';
  patientIndex?: number;
  providerIndex?: number;
  serviceDate?: string;
  claimNumber?: string;
}

/**
 * Generate ISA segment (Interchange Control Header)
 */
function generateISA(controlNumber: string, senderId: string, receiverId: string): string {
  const timestamp = new Date();
  const date = timestamp.toISOString().slice(0, 10).replace(/-/g, '').slice(2); // YYMMDD
  const time = timestamp.toTimeString().slice(0, 5).replace(':', ''); // HHMM
  
  return `ISA*00*          *00*          *ZZ*${senderId.padEnd(15)}*ZZ*${receiverId.padEnd(15)}*${date}*${time}*^*00501*${controlNumber}*0*P*:~`;
}

/**
 * Generate GS segment (Functional Group Header)
 */
function generateGS(controlNumber: string, senderId: string, receiverId: string, transactionType: string): string {
  const timestamp = new Date();
  const date = timestamp.toISOString().slice(0, 10).replace(/-/g, ''); // YYYYMMDD
  const time = timestamp.toTimeString().slice(0, 8).replace(/:/g, ''); // HHMMSS
  
  return `GS*HC*${senderId}*${receiverId}*${date}*${time}*${controlNumber}*X*005010X222A1~`;
}

/**
 * Generate ST segment (Transaction Set Header)
 */
function generateST(controlNumber: string, transactionType: string): string {
  return `ST*${transactionType}*${controlNumber}*005010X222A1~`;
}

/**
 * Generate BHT segment (Beginning of Hierarchical Transaction)
 */
function generateBHT(transactionType: string): string {
  const timestamp = new Date();
  const date = timestamp.toISOString().slice(0, 10).replace(/-/g, ''); // YYYYMMDD
  const time = timestamp.toTimeString().slice(0, 4).replace(':', ''); // HHMM
  
  return `BHT*0019*00*${timestamp.getTime()}*${date}*${time}*CH~`;
}

/**
 * Generate complete 837P (Professional) claim
 */
export function generate837P(options: Claim837Options = { claimType: '837P' }): string {
  const controlNumber = String(Math.floor(Math.random() * 1000000000)).padStart(9, '0');
  const senderId = 'PROVIDER001';
  const receiverId = 'PAYER001';
  
  const provider = SYNTHETIC_PROVIDERS[options.providerIndex || 0];
  const patient = SYNTHETIC_PATIENTS[options.patientIndex || 0];
  const procedure = PROCEDURE_CODES[Math.floor(Math.random() * PROCEDURE_CODES.length)];
  const diagnosis = DIAGNOSIS_CODES[Math.floor(Math.random() * DIAGNOSIS_CODES.length)];
  
  const serviceDate = options.serviceDate || new Date().toISOString().slice(0, 10).replace(/-/g, '');
  const claimNumber = options.claimNumber || `CLM${timestamp()}`;
  
  const segments: string[] = [];
  
  // Interchange and Group Headers
  segments.push(generateISA(controlNumber, senderId, receiverId));
  segments.push(generateGS(controlNumber, senderId, receiverId, '837'));
  segments.push(generateST(controlNumber, '837'));
  segments.push(generateBHT('837'));
  
  // Submitter (HL1)
  segments.push(`NM1*41*2*${provider.name}*****46*${provider.taxId}~`);
  segments.push('PER*IC*BILLING DEPT*TE*5551234567~');
  
  // Receiver (HL2)
  segments.push('NM1*40*2*PAYER NAME*****46*PAYER001~');
  
  // Billing Provider Hierarchical Level (HL)
  segments.push('HL*1**20*1~');
  segments.push(`NM1*85*2*${provider.name}*****XX*${provider.npi}~`);
  segments.push('N3*123 PROVIDER ST~');
  segments.push('N4*ANYTOWN*CA*12345~');
  segments.push(`REF*EI*${provider.taxId}~`);
  
  // Subscriber Hierarchical Level
  segments.push('HL*2*1*22*0~');
  segments.push('SBR*P*18*******MC~');
  segments.push(`NM1*IL*1*${patient.lastName}*${patient.firstName}****MI*${patient.memberId}~`);
  segments.push('N3*456 PATIENT AVE~');
  segments.push('N4*ANYTOWN*CA*12345~');
  segments.push(`DMG*D8*${patient.dob}*M~`);
  
  // Payer
  segments.push('NM1*PR*2*HEALTH PLAN*****PI*PAYER001~');
  
  // Claim Information
  segments.push(`CLM*${claimNumber}*${procedure.amount}***11:B:1*Y*A*Y*Y~`);
  segments.push(`DTP*434*RD8*${serviceDate}-${serviceDate}~`);
  segments.push(`REF*D9*${claimNumber}~`);
  segments.push(`HI*ABK:${diagnosis}~`);
  
  // Service Line
  segments.push(`LX*1~`);
  segments.push(`SV1*HC:${procedure.code}*${procedure.amount}*UN*1***1~`);
  segments.push(`DTP*472*D8*${serviceDate}~`);
  
  // Transaction Set Trailer
  segments.push(`SE*${segments.length - 2}*${controlNumber}~`);
  
  // Functional Group and Interchange Trailers
  segments.push(`GE*1*${controlNumber}~`);
  segments.push(`IEA*1*${controlNumber}~`);
  
  return segments.join('\n');
}

/**
 * Generate complete 837I (Institutional) claim
 */
export function generate837I(options: Claim837Options = { claimType: '837I' }): string {
  const controlNumber = String(Math.floor(Math.random() * 1000000000)).padStart(9, '0');
  const senderId = 'HOSPITAL001';
  const receiverId = 'PAYER001';
  
  const provider = { npi: '1111111119', name: 'GENERAL HOSPITAL', taxId: '111111111' };
  const patient = SYNTHETIC_PATIENTS[options.patientIndex || 0];
  const serviceDate = options.serviceDate || new Date().toISOString().slice(0, 10).replace(/-/g, '');
  const claimNumber = options.claimNumber || `CLM${timestamp()}`;
  
  const segments: string[] = [];
  
  // Interchange and Group Headers
  segments.push(generateISA(controlNumber, senderId, receiverId));
  segments.push(generateGS(controlNumber, senderId, receiverId, '837'));
  segments.push(generateST(controlNumber, '837'));
  segments.push(generateBHT('837'));
  
  // Submitter
  segments.push(`NM1*41*2*${provider.name}*****46*${provider.taxId}~`);
  segments.push('PER*IC*BILLING DEPT*TE*5551234567~');
  
  // Receiver
  segments.push('NM1*40*2*PAYER NAME*****46*PAYER001~');
  
  // Billing Provider Hierarchical Level
  segments.push('HL*1**20*1~');
  segments.push(`NM1*85*2*${provider.name}*****XX*${provider.npi}~`);
  segments.push('N3*789 HOSPITAL BLVD~');
  segments.push('N4*ANYTOWN*CA*12345~');
  segments.push(`REF*EI*${provider.taxId}~`);
  
  // Subscriber Hierarchical Level
  segments.push('HL*2*1*22*0~');
  segments.push('SBR*P*18*******MC~');
  segments.push(`NM1*IL*1*${patient.lastName}*${patient.firstName}****MI*${patient.memberId}~`);
  segments.push('N3*456 PATIENT AVE~');
  segments.push('N4*ANYTOWN*CA*12345~');
  segments.push(`DMG*D8*${patient.dob}*M~`);
  
  // Payer
  segments.push('NM1*PR*2*HEALTH PLAN*****PI*PAYER001~');
  
  // Claim Information
  segments.push(`CLM*${claimNumber}*1500.00***11:B:1*Y*A*Y*Y*P~`);
  segments.push(`DTP*434*RD8*${serviceDate}-${serviceDate}~`);
  segments.push(`REF*D9*${claimNumber}~`);
  segments.push('CL1*1*1*01~');
  segments.push('HI*ABK:E119~');
  
  // Service Line (Inpatient stay)
  segments.push('LX*1~');
  segments.push('SV2*0100*HC:99223*1500.00*UN*1~');
  segments.push(`DTP*472*D8*${serviceDate}~`);
  
  // Transaction Set Trailer
  segments.push(`SE*${segments.length - 2}*${controlNumber}~`);
  
  // Functional Group and Interchange Trailers
  segments.push(`GE*1*${controlNumber}~`);
  segments.push(`IEA*1*${controlNumber}~`);
  
  return segments.join('\n');
}

/**
 * Generate timestamp for unique identifiers
 */
function timestamp(): string {
  return Date.now().toString().slice(-9);
}

/**
 * Batch generate multiple claims
 */
export function generateClaimBatch(count: number, claimType: '837P' | '837I' = '837P'): string[] {
  const claims: string[] = [];
  
  for (let i = 0; i < count; i++) {
    const options: Claim837Options = {
      claimType,
      patientIndex: i % SYNTHETIC_PATIENTS.length,
      providerIndex: i % SYNTHETIC_PROVIDERS.length,
    };
    
    const claim = claimType === '837P' ? generate837P(options) : generate837I(options);
    claims.push(claim);
  }
  
  return claims;
}

/**
 * Save claim to file
 */
export function saveClaimToFile(claim: string, outputPath: string): void {
  const dir = path.dirname(outputPath);
  if (!fs.existsSync(dir)) {
    fs.mkdirSync(dir, { recursive: true });
  }
  
  fs.writeFileSync(outputPath, claim, 'utf-8');
}

/**
 * CLI interface for standalone usage
 */
if (require.main === module) {
  const args = process.argv.slice(2);
  const type = args[0] === '837I' ? '837I' : '837P';
  const count = parseInt(args[1] || '1', 10);
  const outputDir = args[2] || './generated-claims';
  
  console.log(`Generating ${count} ${type} claim(s)...`);
  
  if (!fs.existsSync(outputDir)) {
    fs.mkdirSync(outputDir, { recursive: true });
  }
  
  const claims = generateClaimBatch(count, type);
  
  claims.forEach((claim, index) => {
    const filename = `${type}-claim-${Date.now()}-${index + 1}.edi`;
    const filepath = path.join(outputDir, filename);
    saveClaimToFile(claim, filepath);
    console.log(`✓ Generated: ${filepath}`);
  });
  
  console.log(`\n✅ Successfully generated ${claims.length} claim(s) in ${outputDir}`);
}
