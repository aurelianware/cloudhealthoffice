/**
 * FHIR R4 Integration - Usage Examples
 * 
 * This file contains practical examples for using the FHIR eligibility mapper
 * in real-world scenarios with Cloud Health Office.
 * 
 * Examples include:
 * - Basic X12 270 to FHIR transformation
 * - Integration with Azure Logic Apps
 * - Using fhir.js client library
 * - Handling different member scenarios
 * - Error handling and validation
 */

import { mapX12270ToFhirEligibility } from './fhirEligibilityMapper';
import { X12_270 } from './x12Types';

// Example 1: Basic Subscriber Eligibility Check
export function example_basicSubscriberEligibility(): void {
  console.log('\n=== Example 1: Basic Subscriber Eligibility ===\n');
  
  const x12Input: X12_270 = {
    inquiryId: 'INQ20240115001',
    transactionDate: '20240115-0930',
    informationSource: {
      id: '030240928',
      name: 'Availity Health Network'
    },
    informationReceiver: {
      npi: '1234567890',
      organizationName: 'Austin Medical Center'
    },
    subscriber: {
      memberId: 'THP123456789',
      firstName: 'John',
      lastName: 'Doe',
      dob: '19850615',
      gender: 'M'
    },
    insurerId: 'TXHEALTH01',
    serviceTypeCodes: ['30'] // General health benefit plan coverage
  };

  const { patient, eligibility } = mapX12270ToFhirEligibility(x12Input);
  
  console.log('Patient ID:', patient.id);
  console.log('Patient Name:', patient.name?.[0].given?.[0], patient.name?.[0].family);
  console.log('Birth Date:', patient.birthDate);
  console.log('\nEligibility Request ID:', eligibility.id);
  console.log('Insurer:', eligibility.insurer.display);
  console.log('Service Categories:', eligibility.item?.length);
}

// Example 2: Dependent Eligibility Check
export function example_dependentEligibility(): void {
  console.log('\n=== Example 2: Dependent (Child) Eligibility ===\n');
  
  const x12Input: X12_270 = {
    inquiryId: 'INQ20240115002',
    informationSource: {
      id: '030240928'
    },
    subscriber: {
      memberId: 'FAM987654321', // Family plan member ID
      firstName: 'Robert',
      lastName: 'Johnson',
      dob: '19800101',
      gender: 'M'
    },
    dependent: {
      relationshipCode: '19', // Child
      firstName: 'Emily',
      lastName: 'Johnson',
      dob: '20100515',
      gender: 'F'
    },
    insurerId: 'FAMILYPLAN01',
    serviceTypeCodes: ['30', '64'] // General + Newborn care
  };

  const { patient, eligibility } = mapX12270ToFhirEligibility(x12Input);
  
  console.log('Dependent Name:', patient.name?.[0].given?.[0], patient.name?.[0].family);
  console.log('Dependent DOB:', patient.birthDate);
  console.log('Member ID (Subscriber):', patient.id);
  console.log('Service Types:', eligibility.item?.map(i => i.category?.coding?.[0].display).join(', '));
}

// Example 3: Comprehensive Eligibility with Full Demographics
export function example_comprehensiveEligibility(): void {
  console.log('\n=== Example 3: Comprehensive Eligibility with Demographics ===\n');
  
  const x12Input: X12_270 = {
    inquiryId: 'INQ20240115003',
    transactionDate: '20240115-1430',
    interchangeControlNumber: 'ICN000000123',
    informationSource: {
      id: '030240928',
      name: 'Blue Cross Blue Shield of Texas',
      taxId: '75-1234567'
    },
    informationReceiver: {
      npi: '9876543210',
      organizationName: 'Houston General Hospital',
      taxId: '76-9876543'
    },
    subscriber: {
      memberId: 'BCBS567890123',
      firstName: 'Maria',
      lastName: 'Garcia',
      middleName: 'Elena',
      dob: '19920315',
      gender: 'F',
      ssn: '456-78-9012',
      address: {
        street1: '456 Oak Avenue',
        street2: 'Unit 201',
        city: 'Houston',
        state: 'TX',
        zip: '77002',
        country: 'US'
      },
      phone: '713-555-6789',
      email: 'maria.garcia@example.com'
    },
    insurerId: 'BCBSTX',
    tradingPartnerId: 'AVLTX002',
    serviceTypeCodes: ['1', '48', '49', '86', '98'], // Multiple service types
    serviceDateRange: {
      startDate: '20240101',
      endDate: '20241231'
    }
  };

  const { patient, eligibility } = mapX12270ToFhirEligibility(x12Input);
  
  console.log('Patient Full Name:', 
    patient.name?.[0].given?.join(' '),
    patient.name?.[0].family
  );
  console.log('Address:', patient.address?.[0].line?.join(', '));
  console.log('City, State ZIP:', 
    patient.address?.[0].city,
    patient.address?.[0].state,
    patient.address?.[0].postalCode
  );
  console.log('Phone:', patient.telecom?.find(t => t.system === 'phone')?.value);
  console.log('Email:', patient.telecom?.find(t => t.system === 'email')?.value);
  console.log('\nService Period:', 
    eligibility.servicedPeriod?.start,
    'to',
    eligibility.servicedPeriod?.end
  );
  console.log('Provider NPI:', eligibility.provider?.identifier?.value);
}

// Example 4: Emergency Services Eligibility
export function example_emergencyServicesEligibility(): void {
  console.log('\n=== Example 4: Emergency Services Eligibility Check ===\n');
  
  const x12Input: X12_270 = {
    inquiryId: 'INQ20240115-ER-001',
    transactionDate: '20240115-2245', // Late night emergency
    informationSource: {
      id: '030240928',
      name: 'United Healthcare'
    },
    informationReceiver: {
      npi: '5555555555',
      organizationName: 'County Emergency Medical Center'
    },
    subscriber: {
      memberId: 'UHC445566778',
      firstName: 'James',
      lastName: 'Smith',
      dob: '19750820',
      gender: 'M'
    },
    insurerId: 'UNITEDHC',
    serviceTypeCodes: ['50', '51', '86'], // Hospital Emergency services
    inquiryDetails: {
      certificationType: 'Emergency',
      serviceTypeDescription: 'Emergency Room Visit'
    }
  };

  const { patient, eligibility } = mapX12270ToFhirEligibility(x12Input);
  
  console.log('Emergency Patient:', patient.name?.[0].given?.[0], patient.name?.[0].family);
  console.log('Member ID:', patient.id);
  console.log('Emergency Services Requested:');
  eligibility.item?.forEach(item => {
    console.log(' -', item.category?.coding?.[0].display);
  });
  console.log('Timestamp:', eligibility.created);
}

// Example 5: Pharmacy Benefits Eligibility
export function example_pharmacyEligibility(): void {
  console.log('\n=== Example 5: Pharmacy Benefits Eligibility ===\n');
  
  const x12Input: X12_270 = {
    inquiryId: 'INQ20240115-RX-001',
    informationSource: {
      id: '030240928',
      name: 'CVS Caremark'
    },
    informationReceiver: {
      npi: '1112223333',
      organizationName: 'Walgreens Pharmacy #12345'
    },
    subscriber: {
      memberId: 'CVS789012345',
      firstName: 'Susan',
      lastName: 'Williams',
      dob: '19680505',
      gender: 'F'
    },
    insurerId: 'CVSCAREMARK',
    serviceTypeCodes: ['88', '89', '90', '91'] // Pharmacy services
  };

  const { patient, eligibility } = mapX12270ToFhirEligibility(x12Input);
  
  console.log('Pharmacy Patient:', patient.name?.[0].family + ',', patient.name?.[0].given?.[0]);
  console.log('Pharmacy Benefits Checked:');
  eligibility.item?.forEach(item => {
    console.log(' -', item.category?.coding?.[0].display);
  });
}

// Example 6: Using with fhir.js Client
export async function example_fhirjsIntegration(): Promise<void> {
  console.log('\n=== Example 6: Integration with fhir.js Client ===\n');
  
  // Note: This is a code example - actual execution would require a FHIR server
  const Client = require('fhir.js'); // Would need: npm install fhir.js
  
  // Initialize FHIR client (example configuration)
  const client = Client({
    baseUrl: 'https://your-fhir-server.com/fhir',
    auth: {
      bearer: 'your-oauth-token-here' // Use Azure AD token in production
    }
  });

  const x12Input: X12_270 = {
    inquiryId: 'INQ20240115-FHIRJS-001',
    informationSource: { id: '030240928' },
    subscriber: {
      memberId: 'DEMO123456',
      firstName: 'Demo',
      lastName: 'Patient',
      dob: '1990-01-01',
      gender: 'U'
    },
    insurerId: 'DEMOPLAN'
  };

  const { patient, eligibility } = mapX12270ToFhirEligibility(x12Input);
  
  try {
    console.log('Creating Patient resource on FHIR server...');
    // const patientResponse = await client.create({ resource: patient });
    // console.log('Patient created with ID:', patientResponse.id);

    console.log('Creating CoverageEligibilityRequest...');
    // const eligibilityResponse = await client.create({ resource: eligibility });
    // console.log('Eligibility request created with ID:', eligibilityResponse.id);
    
    console.log('\n(This is a code example - would require actual FHIR server)');
  } catch (error) {
    console.error('Error creating FHIR resources:', error);
  }
}

// Example 7: Batch Processing Multiple Inquiries
export function example_batchProcessing(): void {
  console.log('\n=== Example 7: Batch Processing Multiple Inquiries ===\n');
  
  const inquiries: X12_270[] = [
    {
      inquiryId: 'BATCH-001',
      informationSource: { id: '030240928' },
      subscriber: {
        memberId: 'MEM001',
        firstName: 'Alice',
        lastName: 'Anderson',
        dob: '1985-01-01',
        gender: 'F'
      },
      insurerId: 'BATCH01'
    },
    {
      inquiryId: 'BATCH-002',
      informationSource: { id: '030240928' },
      subscriber: {
        memberId: 'MEM002',
        firstName: 'Bob',
        lastName: 'Brown',
        dob: '1990-02-02',
        gender: 'M'
      },
      insurerId: 'BATCH01'
    },
    {
      inquiryId: 'BATCH-003',
      informationSource: { id: '030240928' },
      subscriber: {
        memberId: 'MEM003',
        firstName: 'Charlie',
        lastName: 'Chen',
        dob: '1995-03-03',
        gender: 'M'
      },
      insurerId: 'BATCH01'
    }
  ];

  console.log(`Processing ${inquiries.length} eligibility inquiries...\n`);
  
  const results = inquiries.map(inquiry => {
    try {
      const { patient, eligibility } = mapX12270ToFhirEligibility(inquiry);
      return {
        inquiryId: inquiry.inquiryId,
        success: true,
        patientId: patient.id,
        eligibilityId: eligibility.id
      };
    } catch (error) {
      return {
        inquiryId: inquiry.inquiryId,
        success: false,
        error: (error as Error).message
      };
    }
  });

  results.forEach(result => {
    if (result.success) {
      console.log(`✅ ${result.inquiryId}: Patient ${result.patientId}, Eligibility ${result.eligibilityId}`);
    } else {
      console.log(`❌ ${result.inquiryId}: Error - ${result.error}`);
    }
  });
  
  const successCount = results.filter(r => r.success).length;
  console.log(`\nProcessed ${successCount}/${results.length} successfully`);
}

// Example 8: Error Handling and Validation
export function example_errorHandling(): void {
  console.log('\n=== Example 8: Error Handling and Validation ===\n');
  
  // Valid inquiry
  const validInput: X12_270 = {
    inquiryId: 'VALID-001',
    informationSource: { id: '030240928' },
    subscriber: {
      memberId: 'MEM12345',
      firstName: 'Valid',
      lastName: 'Patient',
      dob: '1980-01-01'
    },
    insurerId: 'TESTPLAN'
  };

  try {
    const { patient, eligibility } = mapX12270ToFhirEligibility(validInput);
    console.log('✅ Valid inquiry processed successfully');
    console.log('   Patient ID:', patient.id);
    console.log('   Eligibility ID:', eligibility.id);
  } catch (error) {
    console.error('❌ Error processing valid inquiry:', error);
  }

  // Minimal inquiry (edge case)
  const minimalInput: X12_270 = {
    inquiryId: 'MINIMAL-001',
    informationSource: { id: '030240928' },
    subscriber: {
      memberId: 'MIN001',
      firstName: 'Min',
      lastName: 'User',
      dob: '1980-01-01'
    },
    insurerId: 'MIN'
  };

  try {
    const { patient, eligibility } = mapX12270ToFhirEligibility(minimalInput);
    console.log('✅ Minimal inquiry processed successfully');
    console.log('   Required fields only provided');
  } catch (error) {
    console.error('❌ Error processing minimal inquiry:', error);
  }
}

// Example 9: Azure Logic Apps Integration Pattern
export function example_azureLogicAppsIntegration(): void {
  console.log('\n=== Example 9: Azure Logic Apps Integration Pattern ===\n');
  
  console.log('Integration Flow:');
  console.log('1. Logic App receives X12 270 from Service Bus');
  console.log('2. Parse X12 to JSON structure');
  console.log('3. Call TypeScript mapper function');
  console.log('4. Store FHIR resources in Cosmos DB or FHIR server');
  console.log('5. Queue response for X12 271 generation');
  
  // Simulating Logic App workflow
  const x12Input: X12_270 = {
    inquiryId: 'LOGICAPP-001',
    transactionDate: '20240115-1000',
    informationSource: {
      id: '030240928',
      name: 'Availity'
    },
    informationReceiver: {
      npi: '1234567890',
      organizationName: 'Provider Clinic'
    },
    subscriber: {
      memberId: 'LA123456',
      firstName: 'LogicApp',
      lastName: 'User',
      dob: '1985-05-15',
      gender: 'F'
    },
    insurerId: 'LOGICTEST',
    serviceTypeCodes: ['30']
  };

  const { patient, eligibility } = mapX12270ToFhirEligibility(x12Input);
  
  console.log('\nGenerated FHIR Resources:');
  console.log(' - Patient:', patient.id);
  console.log(' - CoverageEligibilityRequest:', eligibility.id);
  console.log('\nReady to persist to storage or send to FHIR server');
}

// Run all examples
export function runAllExamples(): void {
  console.log('╔════════════════════════════════════════════════════════════════╗');
  console.log('║  Cloud Health Office - FHIR R4 Integration Examples           ║');
  console.log('║  X12 270 Eligibility Inquiry → FHIR Patient & Eligibility      ║');
  console.log('╚════════════════════════════════════════════════════════════════╝');
  
  example_basicSubscriberEligibility();
  example_dependentEligibility();
  example_comprehensiveEligibility();
  example_emergencyServicesEligibility();
  example_pharmacyEligibility();
  example_batchProcessing();
  example_errorHandling();
  example_azureLogicAppsIntegration();
  
  console.log('\n╔════════════════════════════════════════════════════════════════╗');
  console.log('║  All examples completed successfully!                          ║');
  console.log('╚════════════════════════════════════════════════════════════════╝\n');
}

// Allow running this file directly
if (require.main === module) {
  runAllExamples();
}
