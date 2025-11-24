/**
 * Synthetic Test Data Generator for Payer-to-Payer Exchange
 * 
 * Generates realistic FHIR R4 synthetic data for testing bulk data operations:
 * - Patient resources (US Core compliant)
 * - Claim resources
 * - ExplanationOfBenefit resources
 * - Encounter resources
 * - ServiceRequest (PriorAuthorizationRequest) resources
 * 
 * Usage:
 *   node dist/scripts/utils/generate-payer-exchange-data.js [count] [output-dir]
 */

import { Patient, Claim, Encounter, ExplanationOfBenefit, ServiceRequest } from 'fhir/r4';
import * as fs from 'fs';
import * as path from 'path';

interface SyntheticDataOptions {
  patientCount: number;
  claimsPerPatient: number;
  encountersPerPatient: number;
  eobsPerPatient: number;
  priorAuthsPerPatient: number;
  outputDir: string;
}

/**
 * Generate synthetic patients with realistic demographics
 */
function generateSyntheticPatients(count: number): Patient[] {
  const patients: Patient[] = [];
  const firstNames = ['John', 'Jane', 'Michael', 'Emily', 'David', 'Sarah', 'Robert', 'Lisa', 'William', 'Jennifer'];
  const lastNames = ['Smith', 'Johnson', 'Williams', 'Brown', 'Jones', 'Garcia', 'Miller', 'Davis', 'Rodriguez', 'Martinez'];
  const cities = ['Seattle', 'Portland', 'San Francisco', 'Los Angeles', 'Phoenix', 'Denver', 'Austin', 'Chicago', 'New York', 'Boston'];
  const states = ['WA', 'OR', 'CA', 'CA', 'AZ', 'CO', 'TX', 'IL', 'NY', 'MA'];

  for (let i = 1; i <= count; i++) {
    const firstName = firstNames[Math.floor(Math.random() * firstNames.length)];
    const lastName = lastNames[Math.floor(Math.random() * lastNames.length)];
    const gender = Math.random() > 0.5 ? 'male' : 'female';
    const birthYear = 1940 + Math.floor(Math.random() * 70);
    const birthMonth = String(Math.floor(Math.random() * 12) + 1).padStart(2, '0');
    const birthDay = String(Math.floor(Math.random() * 28) + 1).padStart(2, '0');
    const cityIndex = Math.floor(Math.random() * cities.length);

    const patient: Patient = {
      resourceType: 'Patient',
      id: `synthetic-patient-${String(i).padStart(6, '0')}`,
      meta: {
        profile: ['http://hl7.org/fhir/us/core/StructureDefinition/us-core-patient'],
        lastUpdated: new Date().toISOString()
      },
      identifier: [
        {
          system: 'http://hospital.example.org/mrn',
          value: `MRN${String(i).padStart(8, '0')}`
        },
        {
          system: 'http://hl7.org/fhir/sid/us-ssn',
          value: `${Math.floor(Math.random() * 900 + 100)}-${Math.floor(Math.random() * 90 + 10)}-${String(i).padStart(4, '0')}`
        },
        {
          system: 'http://payer.example.org/member-id',
          value: `MEM${String(i).padStart(9, '0')}`
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
          value: `${Math.floor(Math.random() * 900 + 100)}-555-${String(Math.floor(Math.random() * 10000)).padStart(4, '0')}`,
          use: 'home'
        },
        {
          system: 'email',
          value: `${firstName.toLowerCase()}.${lastName.toLowerCase()}@example.com`,
          use: 'home'
        }
      ],
      gender: gender as 'male' | 'female',
      birthDate: `${birthYear}-${birthMonth}-${birthDay}`,
      address: [{
        use: 'home',
        line: [`${Math.floor(Math.random() * 9999 + 1)} Main St`],
        city: cities[cityIndex],
        state: states[cityIndex],
        postalCode: `${Math.floor(Math.random() * 90000 + 10000)}`,
        country: 'US'
      }],
      maritalStatus: {
        coding: [{
          system: 'http://terminology.hl7.org/CodeSystem/v3-MaritalStatus',
          ...(Math.random() > 0.5 
            ? { code: 'M', display: 'Married' } 
            : { code: 'S', display: 'Single' })
        }]
      }
    };

    patients.push(patient);
  }

  return patients;
}

/**
 * Generate synthetic claims for patients
 */
function generateSyntheticClaims(patients: Patient[], claimsPerPatient: number): Claim[] {
  const claims: Claim[] = [];
  const claimTypes = ['professional', 'institutional', 'pharmacy', 'vision', 'oral'];

  for (const patient of patients) {
    for (let i = 1; i <= claimsPerPatient; i++) {
      const claimDate = new Date(2024, Math.floor(Math.random() * 12), Math.floor(Math.random() * 28) + 1);
      const claimType = claimTypes[Math.floor(Math.random() * claimTypes.length)];

      const claim: Claim = {
        resourceType: 'Claim',
        id: `synthetic-claim-${patient.id}-${i}`,
        meta: {
          lastUpdated: new Date().toISOString()
        },
        status: 'active',
        type: {
          coding: [{
            system: 'http://terminology.hl7.org/CodeSystem/claim-type',
            code: claimType,
            display: claimType.charAt(0).toUpperCase() + claimType.slice(1)
          }]
        },
        use: 'claim',
        patient: {
          reference: `Patient/${patient.id}`
        },
        billablePeriod: {
          start: claimDate.toISOString().split('T')[0],
          end: claimDate.toISOString().split('T')[0]
        },
        created: claimDate.toISOString().split('T')[0],
        provider: {
          reference: `Organization/provider-${Math.floor(Math.random() * 100) + 1}`
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
            reference: `Coverage/${patient.id}-coverage`
          }
        }],
        item: [{
          sequence: 1,
          productOrService: {
            coding: [{
              system: 'http://www.ama-assn.org/go/cpt',
              code: `${Math.floor(Math.random() * 90000 + 10000)}`,
              display: 'Medical Procedure'
            }]
          },
          servicedDate: claimDate.toISOString().split('T')[0],
          unitPrice: {
            value: Math.floor(Math.random() * 5000 + 100),
            currency: 'USD'
          },
          net: {
            value: Math.floor(Math.random() * 5000 + 100),
            currency: 'USD'
          }
        }],
        total: {
          value: Math.floor(Math.random() * 5000 + 100),
          currency: 'USD'
        }
      };

      claims.push(claim);
    }
  }

  return claims;
}

/**
 * Generate synthetic ExplanationOfBenefit resources
 */
function generateSyntheticEOBs(patients: Patient[], eobsPerPatient: number): ExplanationOfBenefit[] {
  const eobs: ExplanationOfBenefit[] = [];

  for (const patient of patients) {
    for (let i = 1; i <= eobsPerPatient; i++) {
      const eobDate = new Date(2024, Math.floor(Math.random() * 12), Math.floor(Math.random() * 28) + 1);
      const billedAmount = Math.floor(Math.random() * 5000 + 100);
      const allowedAmount = Math.floor(billedAmount * (0.7 + Math.random() * 0.2));
      const paidAmount = Math.floor(allowedAmount * (0.8 + Math.random() * 0.2));

      const eob: ExplanationOfBenefit = {
        resourceType: 'ExplanationOfBenefit',
        id: `synthetic-eob-${patient.id}-${i}`,
        meta: {
          lastUpdated: new Date().toISOString()
        },
        status: 'active',
        type: {
          coding: [{
            system: 'http://terminology.hl7.org/CodeSystem/claim-type',
            code: 'professional'
          }]
        },
        use: 'claim',
        patient: {
          reference: `Patient/${patient.id}`
        },
        billablePeriod: {
          start: eobDate.toISOString().split('T')[0],
          end: eobDate.toISOString().split('T')[0]
        },
        created: eobDate.toISOString(),
        insurer: {
          reference: 'Organization/payer-001'
        },
        provider: {
          reference: `Organization/provider-${Math.floor(Math.random() * 100) + 1}`
        },
        outcome: 'complete',
        insurance: [{
          focal: true,
          coverage: {
            reference: `Coverage/${patient.id}-coverage`
          }
        }],
        item: [{
          sequence: 1,
          productOrService: {
            coding: [{
              system: 'http://www.ama-assn.org/go/cpt',
              code: `${Math.floor(Math.random() * 90000 + 10000)}`
            }]
          },
          servicedDate: eobDate.toISOString().split('T')[0],
          adjudication: [
            {
              category: {
                coding: [{
                  system: 'http://terminology.hl7.org/CodeSystem/adjudication',
                  code: 'submitted'
                }]
              },
              amount: {
                value: billedAmount,
                currency: 'USD'
              }
            },
            {
              category: {
                coding: [{
                  system: 'http://terminology.hl7.org/CodeSystem/adjudication',
                  code: 'eligible'
                }]
              },
              amount: {
                value: allowedAmount,
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
                value: paidAmount,
                currency: 'USD'
              }
            }
          ]
        }],
        total: [
          {
            category: {
              coding: [{
                system: 'http://terminology.hl7.org/CodeSystem/adjudication',
                code: 'submitted'
              }]
            },
            amount: {
              value: billedAmount,
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
              value: paidAmount,
              currency: 'USD'
            }
          }
        ]
      };

      eobs.push(eob);
    }
  }

  return eobs;
}

/**
 * Generate synthetic encounters
 */
function generateSyntheticEncounters(patients: Patient[], encountersPerPatient: number): Encounter[] {
  const encounters: Encounter[] = [];
  const encounterTypes = ['ambulatory', 'emergency', 'inpatient', 'observation'];

  for (const patient of patients) {
    for (let i = 1; i <= encountersPerPatient; i++) {
      const encounterDate = new Date(2024, Math.floor(Math.random() * 12), Math.floor(Math.random() * 28) + 1);
      const encounterType = encounterTypes[Math.floor(Math.random() * encounterTypes.length)];

      const encounter: Encounter = {
        resourceType: 'Encounter',
        id: `synthetic-encounter-${patient.id}-${i}`,
        meta: {
          lastUpdated: new Date().toISOString()
        },
        status: 'finished',
        class: {
          system: 'http://terminology.hl7.org/CodeSystem/v3-ActCode',
          code: encounterType.toUpperCase().substring(0, 3),
          display: encounterType.charAt(0).toUpperCase() + encounterType.slice(1)
        },
        type: [{
          coding: [{
            system: 'http://snomed.info/sct',
            code: '185349003',
            display: 'Encounter for check up'
          }]
        }],
        subject: {
          reference: `Patient/${patient.id}`
        },
        period: {
          start: encounterDate.toISOString(),
          end: new Date(encounterDate.getTime() + 3600000).toISOString() // 1 hour later
        },
        serviceProvider: {
          reference: `Organization/provider-${Math.floor(Math.random() * 100) + 1}`
        }
      };

      encounters.push(encounter);
    }
  }

  return encounters;
}

/**
 * Generate synthetic prior authorization requests (ServiceRequest)
 */
function generateSyntheticPriorAuths(patients: Patient[], priorAuthsPerPatient: number): ServiceRequest[] {
  const priorAuths: ServiceRequest[] = [];

  for (const patient of patients) {
    for (let i = 1; i <= priorAuthsPerPatient; i++) {
      const requestDate = new Date(2024, Math.floor(Math.random() * 12), Math.floor(Math.random() * 28) + 1);

      const priorAuth: ServiceRequest = {
        resourceType: 'ServiceRequest',
        id: `synthetic-priorauth-${patient.id}-${i}`,
        meta: {
          lastUpdated: new Date().toISOString()
        },
        status: Math.random() > 0.3 ? 'active' : 'completed',
        intent: 'order',
        category: [{
          coding: [{
            system: 'http://snomed.info/sct',
            code: '386053000',
            display: 'Evaluation procedure'
          }]
        }],
        priority: 'routine',
        code: {
          coding: [{
            system: 'http://www.ama-assn.org/go/cpt',
            code: `${Math.floor(Math.random() * 90000 + 10000)}`,
            display: 'Medical Service Requiring Authorization'
          }]
        },
        subject: {
          reference: `Patient/${patient.id}`
        },
        authoredOn: requestDate.toISOString(),
        requester: {
          reference: `Practitioner/provider-${Math.floor(Math.random() * 100) + 1}`
        },
        insurance: [{
          reference: `Coverage/${patient.id}-coverage`
        }]
      };

      priorAuths.push(priorAuth);
    }
  }

  return priorAuths;
}

/**
 * Write resources to NDJSON files
 */
function writeNDJSON(resources: any[], filename: string, outputDir: string): void {
  const ndjson = resources.map(r => JSON.stringify(r)).join('\n');
  const filepath = path.join(outputDir, filename);
  fs.writeFileSync(filepath, ndjson, 'utf8');
  // File written successfully
}

/**
 * Main generator function
 */
export function generatePayerExchangeData(options: SyntheticDataOptions): void {
  console.log('Generating synthetic payer-to-payer exchange data...');
  console.log('');

  // Create output directory
  if (!fs.existsSync(options.outputDir)) {
    fs.mkdirSync(options.outputDir, { recursive: true });
  }

  // Generate data
  console.log('Generating patients...');
  const patients = generateSyntheticPatients(options.patientCount);
  writeNDJSON(patients, 'Patient.ndjson', options.outputDir);

  console.log('Generating claims...');
  const claims = generateSyntheticClaims(patients, options.claimsPerPatient);
  writeNDJSON(claims, 'Claim.ndjson', options.outputDir);

  console.log('Generating encounters...');
  const encounters = generateSyntheticEncounters(patients, options.encountersPerPatient);
  writeNDJSON(encounters, 'Encounter.ndjson', options.outputDir);

  console.log('Generating EOBs...');
  const eobs = generateSyntheticEOBs(patients, options.eobsPerPatient);
  writeNDJSON(eobs, 'ExplanationOfBenefit.ndjson', options.outputDir);

  console.log('Generating prior auths...');
  const priorAuths = generateSyntheticPriorAuths(patients, options.priorAuthsPerPatient);
  writeNDJSON(priorAuths, 'ServiceRequest.ndjson', options.outputDir);

  console.log('');
  console.log('✓ Synthetic data generation complete!');
  // Files written to output directory
}

// CLI execution
if (require.main === module) {
  const args = process.argv.slice(2);
  const patientCount = args[0] ? parseInt(args[0]) : 10;
  const outputDir = args[1] || './test-data/payer-exchange';

  const options: SyntheticDataOptions = {
    patientCount,
    claimsPerPatient: 3,
    encountersPerPatient: 5,
    eobsPerPatient: 3,
    priorAuthsPerPatient: 2,
    outputDir
  };

  generatePayerExchangeData(options);
}
