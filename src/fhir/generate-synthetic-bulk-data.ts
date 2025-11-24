/**
 * Synthetic FHIR Bulk Data Generator
 * 
 * Generates synthetic FHIR R4 resources for testing Payer-to-Payer bulk data exchange.
 * Creates NDJSON files for Patient, Claim, Encounter, ExplanationOfBenefit, and ServiceRequest.
 * 
 * Usage:
 *   npm run build
 *   node dist/src/fhir/generate-synthetic-bulk-data.js --count 100 --output ./test-data
 */

import * as fs from 'fs';
import * as path from 'path';
import {
  Patient,
  Claim,
  Encounter,
  ExplanationOfBenefit,
  ServiceRequest,
  Resource
} from 'fhir/r4';

interface GeneratorOptions {
  patientCount: number;
  claimsPerPatient: number;
  encountersPerPatient: number;
  outputDir: string;
}

/**
 * Generate synthetic US Core compliant Patient resources
 */
function generatePatients(count: number): Patient[] {
  const patients: Patient[] = [];
  const firstNames = ['John', 'Jane', 'Michael', 'Sarah', 'David', 'Emily', 'Robert', 'Lisa', 'James', 'Mary'];
  const lastNames = ['Smith', 'Johnson', 'Williams', 'Brown', 'Jones', 'Garcia', 'Miller', 'Davis', 'Rodriguez', 'Martinez'];
  const states = ['TX', 'CA', 'NY', 'FL', 'IL', 'PA', 'OH', 'GA', 'NC', 'MI'];

  for (let i = 1; i <= count; i++) {
    const memberId = `MEM${i.toString().padStart(6, '0')}`;
    const firstName = firstNames[i % firstNames.length];
    const lastName = lastNames[Math.floor(i / firstNames.length) % lastNames.length];
    const gender = i % 2 === 0 ? 'male' : 'female';
    const birthYear = 1940 + (i % 60);
    const birthMonth = (i % 12) + 1;
    const birthDay = (i % 28) + 1;

    const patient: Patient = {
      resourceType: 'Patient',
      id: memberId,
      meta: {
        profile: ['http://hl7.org/fhir/us/core/StructureDefinition/us-core-patient']
      },
      identifier: [
        {
          type: {
            coding: [{
              system: 'http://terminology.hl7.org/CodeSystem/v2-0203',
              code: 'MB',
              display: 'Member Number'
            }]
          },
          system: 'http://example.org/fhir/sid/member-id',
          value: memberId
        },
        {
          type: {
            coding: [{
              system: 'http://terminology.hl7.org/CodeSystem/v2-0203',
              code: 'SB',
              display: 'Social Beneficiary Identifier'
            }]
          },
          system: 'http://hl7.org/fhir/sid/us-ssn',
          value: `${(i % 900 + 100)}-${(i % 90 + 10)}-${(i % 9000 + 1000)}`
        }
      ],
      active: true,
      name: [{
        use: 'official',
        family: lastName,
        given: [firstName]
      }],
      telecom: [
        {
          system: 'phone',
          value: `555-${(i % 900 + 100)}-${(i % 9000 + 1000)}`,
          use: 'home'
        },
        {
          system: 'email',
          value: `${firstName.toLowerCase()}.${lastName.toLowerCase()}@example.com`,
          use: 'home'
        }
      ],
      gender: gender as 'male' | 'female',
      birthDate: `${birthYear}-${birthMonth.toString().padStart(2, '0')}-${birthDay.toString().padStart(2, '0')}`,
      address: [{
        use: 'home',
        line: [`${(i * 123) % 9999} Main St`],
        city: 'Austin',
        state: states[i % states.length],
        postalCode: `${(78700 + (i % 100)).toString()}`
      }]
    };

    patients.push(patient);
  }

  return patients;
}

/**
 * Generate synthetic Claim resources
 */
function generateClaims(patients: Patient[], claimsPerPatient: number): Claim[] {
  const claims: Claim[] = [];
  const claimTypes = ['professional', 'institutional', 'pharmacy'];
  
  for (let patientIndex = 0; patientIndex < patients.length; patientIndex++) {
    const patient = patients[patientIndex];
    for (let i = 1; i <= claimsPerPatient; i++) {
      const claimId = `CLM${patientIndex.toString().padStart(6, '0')}-${i.toString().padStart(3, '0')}`;
      const claimDate = new Date(2023, (patientIndex + i) % 12, (i % 28) + 1);

      const claim: Claim = {
        resourceType: 'Claim',
        id: claimId,
        status: 'active',
        type: {
          coding: [{
            system: 'http://terminology.hl7.org/CodeSystem/claim-type',
            code: claimTypes[i % claimTypes.length]
          }]
        },
        use: 'claim',
        patient: {
          reference: `Patient/${patient.id}`
        },
        created: claimDate.toISOString().split('T')[0],
        provider: {
          identifier: {
            system: 'http://hl7.org/fhir/sid/us-npi',
            value: `${1234567890 + (patientIndex % 1000)}`
          }
        },
        priority: {
          coding: [{
            system: 'http://terminology.hl7.org/CodeSystem/processpriority',
            code: 'normal'
          }]
        },
        insurance: [{
          sequence: 1,
          focal: true,
          coverage: {
            reference: `Coverage/${patient.id}-COV`
          }
        }],
        item: [{
          sequence: 1,
          productOrService: {
            coding: [{
              system: 'http://www.ama-assn.org/go/cpt',
              code: '99213',
              display: 'Office visit'
            }]
          },
          servicedDate: claimDate.toISOString().split('T')[0]
        }],
        total: {
          value: 100 + (i * 50),
          currency: 'USD'
        }
      };

      claims.push(claim);
    }
  }

  return claims;
}

/**
 * Generate synthetic Encounter resources
 */
function generateEncounters(patients: Patient[], encountersPerPatient: number): Encounter[] {
  const encounters: Encounter[] = [];
  const encounterTypes = ['ambulatory', 'emergency', 'inpatient'];
  
  for (let patientIndex = 0; patientIndex < patients.length; patientIndex++) {
    const patient = patients[patientIndex];
    for (let i = 1; i <= encountersPerPatient; i++) {
      const encounterId = `ENC${patientIndex.toString().padStart(6, '0')}-${i.toString().padStart(3, '0')}`;
      const encounterDate = new Date(2023, (patientIndex + i) % 12, (i % 28) + 1);

      const encounter: Encounter = {
        resourceType: 'Encounter',
        id: encounterId,
        status: 'finished',
        class: {
          system: 'http://terminology.hl7.org/CodeSystem/v3-ActCode',
          code: encounterTypes[i % encounterTypes.length].toUpperCase().substring(0, 3),
          display: encounterTypes[i % encounterTypes.length]
        },
        type: [{
          coding: [{
            system: 'http://snomed.info/sct',
            code: '185347001',
            display: 'Encounter for problem'
          }]
        }],
        subject: {
          reference: `Patient/${patient.id}`
        },
        period: {
          start: encounterDate.toISOString(),
          end: new Date(encounterDate.getTime() + 3600000).toISOString() // 1 hour later
        }
      };

      encounters.push(encounter);
    }
  }

  return encounters;
}

/**
 * Generate synthetic ExplanationOfBenefit resources
 */
function generateEOBs(claims: Claim[]): ExplanationOfBenefit[] {
  const eobs: ExplanationOfBenefit[] = [];

  claims.forEach((claim, index) => {
    const eobId = `EOB${claim.id}`;
    const processedDate = new Date(claim.created!);
    processedDate.setDate(processedDate.getDate() + 14); // 14 days after claim submission

    // Ensure claim has insurance before accessing
    if (!claim.insurance || claim.insurance.length === 0) {
      return; // Skip this claim if no insurance
    }

    const eob: ExplanationOfBenefit = {
      resourceType: 'ExplanationOfBenefit',
      id: eobId,
      status: 'active',
      type: claim.type,
      use: 'claim',
      patient: claim.patient,
      created: processedDate.toISOString().split('T')[0],
      insurer: {
        identifier: {
          system: 'http://example.org/fhir/sid/payer-id',
          value: 'PAYER001'
        }
      },
      provider: claim.provider,
      outcome: 'complete',
      insurance: [{
        focal: true,
        coverage: claim.insurance[0].coverage
      }],
      item: claim.item?.map((item, idx) => ({
        sequence: item.sequence,
        category: {
          coding: [{
            system: 'http://terminology.hl7.org/CodeSystem/ex-benefitcategory',
            code: '1',
            display: 'Medical Care'
          }]
        },
        productOrService: item.productOrService,
        servicedDate: item.servicedDate,
        adjudication: [
          {
            category: {
              coding: [{
                system: 'http://terminology.hl7.org/CodeSystem/adjudication',
                code: 'submitted'
              }]
            },
            amount: {
              value: claim.total!.value,
              currency: 'USD'
            }
          },
          {
            category: {
              coding: [{
                system: 'http://terminology.hl7.org/CodeSystem/adjudication',
                code: 'benefit'
              }]
            },
            amount: {
              value: claim.total!.value! * 0.8, // 80% covered
              currency: 'USD'
            }
          }
        ]
      }))
    };

    eobs.push(eob);
  });

  return eobs;
}

/**
 * Generate synthetic ServiceRequest (Prior Authorization) resources
 */
function generateServiceRequests(patients: Patient[]): ServiceRequest[] {
  const serviceRequests: ServiceRequest[] = [];

  patients.forEach((patient, index) => {
    if (index % 3 === 0) { // Generate PA for every 3rd patient
      const srId = `SR${patient.id}`;
      const requestDate = new Date(2024, index % 12, (index % 28) + 1);

      const serviceRequest: ServiceRequest = {
        resourceType: 'ServiceRequest',
        id: srId,
        status: 'active',
        intent: 'order',
        code: {
          coding: [{
            system: 'http://snomed.info/sct',
            code: '386053000',
            display: 'Evaluation procedure'
          }]
        },
        subject: {
          reference: `Patient/${patient.id}`
        },
        authoredOn: requestDate.toISOString().split('T')[0],
        requester: {
          identifier: {
            system: 'http://hl7.org/fhir/sid/us-npi',
            value: `${1234567890 + (index % 1000)}`
          }
        }
      };

      serviceRequests.push(serviceRequest);
    }
  });

  return serviceRequests;
}

/**
 * Write resources to NDJSON file
 */
function writeNDJSON(resources: Resource[], filepath: string): void {
  const ndjson = resources.map(r => JSON.stringify(r)).join('\n');
  fs.writeFileSync(filepath, ndjson, 'utf8');
  console.log(`✓ Generated ${resources.length} resources → ${filepath}`);
}

/**
 * Main generator function
 */
export function generateSyntheticBulkData(options: GeneratorOptions): void {
  console.log('🏥 Generating Synthetic FHIR Bulk Data for Payer-to-Payer Testing...\n');

  // Create output directory if it doesn't exist
  if (!fs.existsSync(options.outputDir)) {
    fs.mkdirSync(options.outputDir, { recursive: true });
  }

  // Generate resources
  console.log(`Generating ${options.patientCount} patients...`);
  const patients = generatePatients(options.patientCount);
  writeNDJSON(patients, path.join(options.outputDir, 'Patient.ndjson'));

  console.log(`Generating ${options.patientCount * options.claimsPerPatient} claims...`);
  const claims = generateClaims(patients, options.claimsPerPatient);
  writeNDJSON(claims, path.join(options.outputDir, 'Claim.ndjson'));

  console.log(`Generating ${options.patientCount * options.encountersPerPatient} encounters...`);
  const encounters = generateEncounters(patients, options.encountersPerPatient);
  writeNDJSON(encounters, path.join(options.outputDir, 'Encounter.ndjson'));

  console.log(`Generating ${claims.length} EOBs...`);
  const eobs = generateEOBs(claims);
  writeNDJSON(eobs, path.join(options.outputDir, 'ExplanationOfBenefit.ndjson'));

  console.log(`Generating service requests (prior authorizations)...`);
  const serviceRequests = generateServiceRequests(patients);
  writeNDJSON(serviceRequests, path.join(options.outputDir, 'ServiceRequest.ndjson'));

  console.log(`\n✅ Synthetic bulk data generation complete!`);
  console.log(`📁 Output directory: ${options.outputDir}`);
  console.log(`📊 Summary:`);
  console.log(`   - ${patients.length} Patients`);
  console.log(`   - ${claims.length} Claims`);
  console.log(`   - ${encounters.length} Encounters`);
  console.log(`   - ${eobs.length} ExplanationOfBenefits`);
  console.log(`   - ${serviceRequests.length} ServiceRequests (Prior Auths)`);
}

// CLI execution
if (require.main === module) {
  const args = process.argv.slice(2);
  const options: GeneratorOptions = {
    patientCount: 100,
    claimsPerPatient: 3,
    encountersPerPatient: 2,
    outputDir: './test-data/bulk-export'
  };

  // Parse CLI arguments
  for (let i = 0; i < args.length; i += 2) {
    const flag = args[i];
    const value = args[i + 1];

    switch (flag) {
      case '--count':
      case '-c':
        options.patientCount = parseInt(value, 10);
        break;
      case '--claims':
        options.claimsPerPatient = parseInt(value, 10);
        break;
      case '--encounters':
        options.encountersPerPatient = parseInt(value, 10);
        break;
      case '--output':
      case '-o':
        options.outputDir = value;
        break;
      case '--help':
      case '-h':
        console.log(`
Synthetic FHIR Bulk Data Generator

Usage: node dist/src/fhir/generate-synthetic-bulk-data.js [options]

Options:
  -c, --count <n>        Number of patients to generate (default: 100)
  --claims <n>           Number of claims per patient (default: 3)
  --encounters <n>       Number of encounters per patient (default: 2)
  -o, --output <dir>     Output directory (default: ./test-data/bulk-export)
  -h, --help             Show this help message

Examples:
  # Generate 50 patients with default claims and encounters
  node dist/src/fhir/generate-synthetic-bulk-data.js --count 50

  # Generate 200 patients with 5 claims each
  node dist/src/fhir/generate-synthetic-bulk-data.js -c 200 --claims 5

  # Generate to custom directory
  node dist/src/fhir/generate-synthetic-bulk-data.js -o ./my-test-data
        `);
        process.exit(0);
    }
  }

  generateSyntheticBulkData(options);
}
