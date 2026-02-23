#!/usr/bin/env node

const fs = require('fs');
const path = require('path');
const X12Parser = require('x12-parser');

const INPUT_DIR = process.env.INPUT_DIR || '/input';
const OUTPUT_DIR = process.env.OUTPUT_DIR || '/output';

console.log(`[834 Parser] Starting - Input: ${INPUT_DIR}, Output: ${OUTPUT_DIR}`);

function parse834Transaction(fileContent, fileName) {
  const parser = new X12Parser();
  const interchange = parser.parseX12(fileContent);
  
  const enrollments = [];
  
  // X12 834 structure: ISA -> GS -> ST -> BGN -> REF -> DTP -> N1 -> INS -> REF -> DTP -> NM1 -> N3 -> N4 -> DMG -> HD -> SE -> GE -> IEA
  
  interchange.functionalGroups.forEach(group => {
    group.transactions.forEach(transaction => {
      if (transaction.transactionSetCode !== '834') {
        console.warn(`[834 Parser] Skipping non-834 transaction: ${transaction.transactionSetCode}`);
        return;
      }
      
      // BGN - Beginning Segment
      const bgn = transaction.segments.find(s => s.tag === 'BGN');
      const transactionType = bgn?.elements[1]; // 00=Original, 04=Change, 21=Information Copy
      
      // REF - Reference Information (Sponsor/Employer)
      const sponsorRef = transaction.segments.find(s => s.tag === 'REF' && s.elements[1] === '0F');
      const sponsorId = sponsorRef?.elements[2];
      
      // DTP - Date/Time Reference (Enrollment Period)
      const effectiveDate = transaction.segments.find(s => s.tag === 'DTP' && s.elements[1] === '007');
      
      // N1 - Party Identification (Sponsor/Payer)
      let currentSponsor = null;
      let currentMember = null;
      let currentDependent = null;
      
      transaction.segments.forEach(segment => {
        switch (segment.tag) {
          case 'N1':
            // Party Name (Sponsor/Payer)
            if (segment.elements[1] === 'P5') { // Plan Sponsor
              currentSponsor = {
                qualifier: segment.elements[1],
                name: segment.elements[2],
                idQualifier: segment.elements[3],
                id: segment.elements[4]
              };
            }
            break;
            
          case 'INS':
            // Member Level Detail
            if (currentMember) {
              enrollments.push(currentMember); // Save previous member
            }
            
            currentMember = {
              relationship: segment.elements[2], // 18=Employee, 01=Spouse, 19=Child
              maintenanceType: segment.elements[3], // 001=Change, 021=Addition, 024=Cancellation/Termination
              maintenanceReason: segment.elements[4],
              benefitStatus: segment.elements[5], // A=Active, C=COBRA, T=Terminated
              enrollmentDate: null,
              terminationDate: null,
              demographics: {},
              coverage: [],
              dependents: []
            };
            
            // Link to sponsor
            if (currentSponsor) {
              currentMember.sponsor = currentSponsor;
            }
            break;
            
          case 'REF':
            // Reference - Member ID, SSN, etc.
            if (currentMember) {
              const qualifier = segment.elements[1];
              const value = segment.elements[2];
              
              if (qualifier === '0F') {
                currentMember.subscriberId = value;
              } else if (qualifier === '1L') {
                currentMember.groupNumber = value;
              } else if (qualifier === 'ZZ') {
                currentMember.employeeId = value;
              }
            }
            break;
            
          case 'DTP':
            // Date/Time Reference
            if (currentMember) {
              const dateQualifier = segment.elements[1];
              const dateFormat = segment.elements[2]; // D8=CCYYMMDD, RD8=Range
              const date = segment.elements[3];
              
              if (dateQualifier === '303') {
                // Maintenance Effective Date
                currentMember.enrollmentDate = parseX12Date(date);
              } else if (dateQualifier === '357') {
                // Employment Begin Date
                currentMember.employmentStartDate = parseX12Date(date);
              } else if (dateQualifier === '356') {
                // Employment End Date (Termination)
                currentMember.terminationDate = parseX12Date(date);
              }
            }
            break;
            
          case 'NM1':
            // Individual Name
            if (currentMember) {
              const entityQualifier = segment.elements[1];
              
              if (entityQualifier === 'IL') {
                // Insured/Subscriber
                currentMember.demographics = {
                  entityType: segment.elements[2], // 1=Person, 2=Non-Person Entity
                  lastName: segment.elements[3],
                  firstName: segment.elements[4],
                  middleName: segment.elements[5],
                  suffix: segment.elements[7],
                  idQualifier: segment.elements[8],
                  id: segment.elements[9] // SSN or Member ID
                };
              } else if (entityQualifier === '70') {
                // Dependent
                currentDependent = {
                  entityType: segment.elements[2],
                  lastName: segment.elements[3],
                  firstName: segment.elements[4],
                  middleName: segment.elements[5],
                  suffix: segment.elements[7],
                  idQualifier: segment.elements[8],
                  id: segment.elements[9]
                };
              }
            }
            break;
            
          case 'N3':
            // Address Information
            if (currentMember && currentMember.demographics) {
              currentMember.demographics.address1 = segment.elements[1];
              currentMember.demographics.address2 = segment.elements[2];
            } else if (currentDependent) {
              currentDependent.address1 = segment.elements[1];
              currentDependent.address2 = segment.elements[2];
            }
            break;
            
          case 'N4':
            // Geographic Location (City, State, Zip)
            if (currentMember && currentMember.demographics) {
              currentMember.demographics.city = segment.elements[1];
              currentMember.demographics.state = segment.elements[2];
              currentMember.demographics.zip = segment.elements[3];
            } else if (currentDependent) {
              currentDependent.city = segment.elements[1];
              currentDependent.state = segment.elements[2];
              currentDependent.zip = segment.elements[3];
            }
            break;
            
          case 'DMG':
            // Demographic Information
            if (currentMember && currentMember.demographics) {
              currentMember.demographics.dateOfBirth = parseX12Date(segment.elements[2]);
              currentMember.demographics.gender = segment.elements[3]; // M, F, U
            } else if (currentDependent) {
              currentDependent.dateOfBirth = parseX12Date(segment.elements[2]);
              currentDependent.gender = segment.elements[3];
            }
            break;
            
          case 'HD':
            // Health Coverage (Plan/Benefit Info)
            const coverage = {
              maintenanceType: segment.elements[1], // 001=Add, 002=Delete, 021=Addition
              maintenanceReason: segment.elements[2],
              insuranceLineCode: segment.elements[3], // HLT=Health, DEN=Dental, VIS=Vision
              planCoverageDescription: segment.elements[4],
              coverageLevel: segment.elements[5] // EMP=Employee Only, ESP=Employee+Spouse, ECH=Employee+Children, FAM=Family
            };
            
            if (currentMember) {
              currentMember.coverage.push(coverage);
            } else if (currentDependent) {
              if (!currentDependent.coverage) currentDependent.coverage = [];
              currentDependent.coverage.push(coverage);
            }
            break;
            
          case 'LS':
            // Loop Start - Dependent information follows
            break;
            
          case 'LE':
            // Loop End - End of dependent
            if (currentDependent && currentMember) {
              currentMember.dependents.push(currentDependent);
              currentDependent = null;
            }
            break;
        }
      });
      
      // Add last member
      if (currentMember) {
        enrollments.push(currentMember);
      }
    });
  });
  
  return {
    fileName: fileName,
    parsedAt: new Date().toISOString(),
    transactionCount: enrollments.length,
    enrollments: enrollments
  };
}

function parseX12Date(dateString) {
  if (!dateString) return null;
  
  // Handle range dates (RD8 format: CCYYMMDD-CCYYMMDD)
  if (dateString.includes('-')) {
    const [start, end] = dateString.split('-');
    return {
      start: parseX12Date(start),
      end: parseX12Date(end)
    };
  }
  
  // D8 format: CCYYMMDD
  if (dateString.length === 8) {
    const year = dateString.substring(0, 4);
    const month = dateString.substring(4, 6);
    const day = dateString.substring(6, 8);
    return `${year}-${month}-${day}`;
  }
  
  return dateString;
}

function processFiles() {
  const files = fs.readdirSync(INPUT_DIR).filter(f => f.endsWith('.edi') || f.endsWith('.x12') || f.endsWith('.834'));
  
  console.log(`[834 Parser] Found ${files.length} files to process`);
  
  files.forEach(file => {
    const inputPath = path.join(INPUT_DIR, file);
    const outputPath = path.join(OUTPUT_DIR, file.replace(/\.(edi|x12|834)$/, '.json'));
    
    try {
      console.log(`[834 Parser] Processing: ${file}`);
      const content = fs.readFileSync(inputPath, 'utf8');
      const result = parse834Transaction(content, file);
      
      fs.writeFileSync(outputPath, JSON.stringify(result, null, 2));
      console.log(`[834 Parser] ✓ Parsed ${result.transactionCount} enrollments -> ${outputPath}`);
    } catch (error) {
      console.error(`[834 Parser] ✗ Error processing ${file}:`, error.message);
      
      // Write error file
      const errorPath = path.join(OUTPUT_DIR, file.replace(/\.(edi|x12|834)$/, '.error.json'));
      fs.writeFileSync(errorPath, JSON.stringify({
        fileName: file,
        error: error.message,
        stack: error.stack
      }, null, 2));
    }
  });
  
  console.log('[834 Parser] Processing complete');
}

// Ensure output directory exists
if (!fs.existsSync(OUTPUT_DIR)) {
  fs.mkdirSync(OUTPUT_DIR, { recursive: true });
}

processFiles();
