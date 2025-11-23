import { Patient, CoverageEligibilityRequest, Identifier, HumanName, Address, ContactPoint } from 'fhir/r4';
import { X12_270 } from './x12Types';

/**
 * Comprehensive mapping function: X12 270 EDI Eligibility Inquiry → FHIR R4 Patient & CoverageEligibilityRequest
 * 
 * This implementation supports CMS Interoperability and Patient Access final rule requirements
 * for payer systems to provide FHIR R4 APIs for eligibility and coverage information.
 * 
 * References:
 * - CMS-9115-F: Patient Access and Interoperability Final Rule
 * - X12 005010X279A1 Health Care Eligibility Benefit Inquiry (270)
 * - HL7 FHIR R4 Specification (v4.0.1)
 * - Da Vinci Payer Data Exchange (PDex) Implementation Guide
 * 
 * @param input X12 270 EDI eligibility inquiry structure
 * @returns Object containing FHIR R4 Patient and CoverageEligibilityRequest resources
 */
export function mapX12270ToFhirEligibility(input: X12_270): { 
  patient: Patient; 
  eligibility: CoverageEligibilityRequest 
} {
  // Determine if we're mapping subscriber or dependent
  const member = input.dependent || input.subscriber;
  const isDependent = !!input.dependent;
  
  // Map Patient resource with comprehensive demographics
  const patient: Patient = mapToPatient(member, input.subscriber.memberId, isDependent, input);
  
  // Map CoverageEligibilityRequest resource
  const eligibility: CoverageEligibilityRequest = mapToEligibilityRequest(input, patient.id!);
  
  return { patient, eligibility };
}

/**
 * Maps X12 270 member/subscriber data to FHIR R4 Patient resource
 * Includes demographics, identifiers, contact information, and address
 */
function mapToPatient(
  member: X12_270['subscriber'] | NonNullable<X12_270['dependent']>,
  memberId: string,
  isDependent: boolean,
  input: X12_270
): Patient {
  const patient: Patient = {
    resourceType: 'Patient',
    id: memberId,
    meta: {
      profile: ['http://hl7.org/fhir/us/core/StructureDefinition/us-core-patient']
    },
    
    // Identifiers including member ID and SSN (if available)
    identifier: buildIdentifiers(member, memberId, input),
    
    // Active status (assume active for eligibility inquiries)
    active: true,
    
    // Name with proper structure
    name: buildName(member),
    
    // Telecom (phone, email)
    telecom: buildTelecom(member),
    
    // Gender mapping from X12 to FHIR
    gender: mapGender(member.gender),
    
    // Birth date in YYYY-MM-DD format
    birthDate: normalizeDateFormat(member.dob),
    
    // Address information
    address: buildAddress(member)
  };
  
  return patient;
}

/**
 * Maps X12 270 inquiry to FHIR R4 CoverageEligibilityRequest
 * Includes service types, date ranges, and payer information
 */
function mapToEligibilityRequest(input: X12_270, patientId: string): CoverageEligibilityRequest {
  const eligibility: CoverageEligibilityRequest = {
    resourceType: 'CoverageEligibilityRequest',
    id: input.inquiryId,
    meta: {
      profile: ['http://hl7.org/fhir/StructureDefinition/CoverageEligibilityRequest']
    },
    
    // Status: active for newly created requests
    status: 'active',
    
    // Priority: routine for standard eligibility inquiries
    priority: {
      coding: [{
        system: 'http://terminology.hl7.org/CodeSystem/processpriority',
        code: 'normal',
        display: 'Normal'
      }]
    },
    
    // Purpose: validation and benefits inquiry
    purpose: ['validation', 'benefits'],
    
    // Patient reference
    patient: {
      reference: `Patient/${patientId}`,
      identifier: {
        system: 'http://terminology.hl7.org/CodeSystem/v2-0203',
        value: input.subscriber.memberId
      }
    },
    
    // Created timestamp
    created: input.transactionDate 
      ? parseX12DateTime(input.transactionDate)
      : new Date().toISOString(),
    
    // Enterer/Provider making the inquiry
    enterer: input.informationReceiver?.npi ? {
      identifier: {
        system: 'http://hl7.org/fhir/sid/us-npi',
        value: input.informationReceiver.npi
      }
    } : undefined,
    
    // Provider requesting information
    provider: input.informationReceiver?.npi ? {
      identifier: {
        system: 'http://hl7.org/fhir/sid/us-npi',
        value: input.informationReceiver.npi
      }
    } : undefined,
    
    // Insurer/Payer being queried
    insurer: {
      identifier: {
        system: 'http://terminology.hl7.org/CodeSystem/v2-0203',
        value: input.insurerId,
        type: {
          coding: [{
            system: 'http://terminology.hl7.org/CodeSystem/v2-0203',
            code: 'NIIP',
            display: 'National Insurance Payor Identifier (Payor)'
          }]
        }
      },
      display: input.informationSource.name
    },
    
    // Service category and type based on X12 service type codes
    item: buildServiceItems(input)
  };
  
  // Add service period if date range provided
  if (input.serviceDateRange?.startDate || input.serviceDateRange?.endDate) {
    eligibility.servicedPeriod = {
      start: input.serviceDateRange.startDate 
        ? normalizeDateFormat(input.serviceDateRange.startDate)
        : undefined,
      end: input.serviceDateRange.endDate
        ? normalizeDateFormat(input.serviceDateRange.endDate)
        : undefined
    };
  }
  
  return eligibility;
}

/**
 * Builds FHIR Identifier array from X12 member data
 */
function buildIdentifiers(
  member: X12_270['subscriber'] | NonNullable<X12_270['dependent']>,
  memberId: string,
  input: X12_270
): Identifier[] {
  const identifiers: Identifier[] = [
    {
      use: 'official',
      type: {
        coding: [{
          system: 'http://terminology.hl7.org/CodeSystem/v2-0203',
          code: 'MB',
          display: 'Member Number'
        }]
      },
      system: `urn:oid:${input.insurerId}`,
      value: memberId
    }
  ];
  
  // Add SSN if available (for matching purposes only, handle with care per HIPAA)
  if ('ssn' in member && member.ssn) {
    identifiers.push({
      use: 'official',
      type: {
        coding: [{
          system: 'http://terminology.hl7.org/CodeSystem/v2-0203',
          code: 'SS',
          display: 'Social Security Number'
        }]
      },
      system: 'http://hl7.org/fhir/sid/us-ssn',
      value: member.ssn
    });
  }
  
  return identifiers;
}

/**
 * Builds FHIR HumanName array from X12 member name data
 */
function buildName(member: X12_270['subscriber'] | NonNullable<X12_270['dependent']>): HumanName[] {
  const names: HumanName[] = [{
    use: 'official',
    family: member.lastName,
    given: member.middleName 
      ? [member.firstName, member.middleName]
      : [member.firstName]
  }];
  
  return names;
}

/**
 * Builds FHIR ContactPoint array (telecom) from X12 contact data
 */
function buildTelecom(member: X12_270['subscriber'] | NonNullable<X12_270['dependent']>): ContactPoint[] | undefined {
  const telecom: ContactPoint[] = [];
  
  if ('phone' in member && member.phone) {
    telecom.push({
      system: 'phone',
      value: member.phone,
      use: 'home'
    });
  }
  
  if ('email' in member && member.email) {
    telecom.push({
      system: 'email',
      value: member.email,
      use: 'home'
    });
  }
  
  return telecom.length > 0 ? telecom : undefined;
}

/**
 * Builds FHIR Address array from X12 address data
 */
function buildAddress(member: X12_270['subscriber'] | NonNullable<X12_270['dependent']>): Address[] | undefined {
  if (!('address' in member) || !member.address) {
    return undefined;
  }
  
  const addr = member.address;
  if (!addr.street1 && !addr.city && !addr.state && !addr.zip) {
    return undefined;
  }
  
  const address: Address = {
    use: 'home',
    type: 'physical',
    line: [addr.street1, addr.street2].filter(Boolean) as string[],
    city: addr.city,
    state: addr.state,
    postalCode: addr.zip,
    country: addr.country || 'US'
  };
  
  return [address];
}

/**
 * Maps X12 gender codes to FHIR gender codes
 * X12: M=Male, F=Female, U=Unknown
 * FHIR: male | female | other | unknown
 */
function mapGender(gender?: string): Patient['gender'] {
  if (!gender) return 'unknown';
  
  const g = gender.toUpperCase();
  switch (g) {
    case 'M':
    case 'MALE':
      return 'male';
    case 'F':
    case 'FEMALE':
      return 'female';
    case 'U':
    case 'UNKNOWN':
      return 'unknown';
    default:
      return 'other';
  }
}

/**
 * Normalizes X12 date format (CCYYMMDD) to FHIR date format (YYYY-MM-DD)
 */
function normalizeDateFormat(dateStr: string): string {
  // Already in YYYY-MM-DD format
  if (dateStr.includes('-') && dateStr.length === 10) {
    return dateStr;
  }
  
  // Convert CCYYMMDD to YYYY-MM-DD
  if (dateStr.length === 8) {
    return `${dateStr.substring(0, 4)}-${dateStr.substring(4, 6)}-${dateStr.substring(6, 8)}`;
  }
  
  return dateStr;
}

/**
 * Parses X12 date-time format (CCYYMMDD-HHMM) to ISO 8601
 */
function parseX12DateTime(dateTime: string): string {
  const [date, time] = dateTime.split('-');
  const normalizedDate = normalizeDateFormat(date);
  
  if (time && time.length === 4) {
    return `${normalizedDate}T${time.substring(0, 2)}:${time.substring(2, 4)}:00Z`;
  }
  
  return `${normalizedDate}T00:00:00Z`;
}

/**
 * Builds service item array from X12 service type codes
 * Maps X12 service types to FHIR service categories
 */
function buildServiceItems(input: X12_270): CoverageEligibilityRequest['item'] {
  if (!input.serviceTypeCodes || input.serviceTypeCodes.length === 0) {
    // Default to health benefit plan coverage inquiry
    return [{
      category: {
        coding: [{
          system: 'http://terminology.hl7.org/CodeSystem/ex-benefitcategory',
          code: '30',
          display: 'Health Benefit Plan Coverage'
        }]
      }
    }];
  }
  
  // Map each X12 service type code to FHIR benefit category
  return input.serviceTypeCodes.map(code => ({
    category: {
      coding: [{
        system: 'https://x12.org/codes/service-type-codes',
        code: code,
        display: getServiceTypeDisplay(code)
      }]
    }
  }));
}

/**
 * Gets display name for X12 service type codes
 * Common codes: 30=General, 1=Medical Care, 2=Surgical, 33=Chiropractic, etc.
 */
function getServiceTypeDisplay(code: string): string {
  const serviceTypes: Record<string, string> = {
    '1': 'Medical Care',
    '2': 'Surgical',
    '3': 'Consultation',
    '4': 'Diagnostic X-Ray',
    '5': 'Diagnostic Lab',
    '6': 'Radiation Therapy',
    '7': 'Anesthesia',
    '8': 'Surgical Assistance',
    '9': 'Other Medical',
    '10': 'Blood Charges',
    '11': 'Used Durable Medical Equipment',
    '12': 'Durable Medical Equipment Purchase',
    '13': 'Ambulatory Service Center Facility',
    '14': 'Renal Supplies in the Home',
    '15': 'Alternate Method Dialysis',
    '16': 'Chronic Renal Disease (CRD) Equipment',
    '17': 'Pre-Admission Testing',
    '18': 'Durable Medical Equipment Rental',
    '19': 'Pneumonia Vaccine',
    '20': 'Second Surgical Opinion',
    '21': 'Third Surgical Opinion',
    '22': 'Social Work',
    '23': 'Diagnostic Dental',
    '24': 'Periodontics',
    '25': 'Restorative',
    '26': 'Endodontics',
    '27': 'Maxillofacial Prosthetics',
    '28': 'Adjunctive Dental Services',
    '30': 'Health Benefit Plan Coverage',
    '33': 'Chiropractic',
    '35': 'Dental Care',
    '36': 'Dental Crowns',
    '37': 'Dental Accident',
    '38': 'Orthodontics',
    '39': 'Prosthodontics',
    '40': 'Oral Surgery',
    '41': 'Routine (Preventive) Dental',
    '42': 'Home Health Care',
    '43': 'Home Health Prescriptions',
    '44': 'Home Health Visits',
    '45': 'Hospice',
    '46': 'Respite Care',
    '47': 'Hospital',
    '48': 'Hospital - Inpatient',
    '49': 'Hospital - Outpatient',
    '50': 'Hospital - Emergency Accident',
    '51': 'Hospital - Emergency Medical',
    '52': 'Hospital - Ambulatory Surgical',
    '53': 'Long Term Care',
    '54': 'Major Medical',
    '55': 'Medically Related Transportation',
    '56': 'Air Transportation',
    '57': 'Cabulance',
    '58': 'Licensed Ambulance',
    '59': 'General Benefits',
    '60': 'In-vitro Fertilization',
    '61': 'MRI/CAT Scan',
    '62': 'Donor Procedures',
    '63': 'Acupuncture',
    '64': 'Newborn Care',
    '65': 'Pathology',
    '66': 'Smoking Cessation',
    '67': 'Well Baby Care',
    '68': 'Maternity',
    '69': 'Transplants',
    '70': 'Audiology Exam',
    '71': 'Inhalation Therapy',
    '72': 'Diagnostic Medical',
    '73': 'Private Duty Nursing',
    '74': 'Prosthetic Device',
    '75': 'Dialysis',
    '76': 'Otological Exam',
    '77': 'Chemotherapy',
    '78': 'Allergy Testing',
    '79': 'Immunizations',
    '80': 'Routine Physical',
    '81': 'Family Planning',
    '82': 'Infertility',
    '83': 'Abortion',
    '84': 'AIDS',
    '85': 'Emergency Services',
    '86': 'Cancer',
    '87': 'Pharmacy',
    '88': 'Free Standing Prescription Drug',
    '89': 'Mail Order Prescription Drug',
    '90': 'Brand Name Prescription Drug',
    '91': 'Generic Prescription Drug',
    '92': 'Podiatry',
    '93': 'Podiatry - Office Visits',
    '94': 'Podiatry - Nursing Home Visits',
    '95': 'Professional (Physician)',
    '96': 'Anesthesiologist',
    '97': 'Professional (Physician) Visit - Office',
    '98': 'Professional (Physician) Visit - Inpatient',
    '99': 'Professional (Physician) Visit - Outpatient',
    'A0': 'Professional (Physician) Visit - Nursing Home',
    'A1': 'Professional (Physician) Visit - Skilled Nursing Facility',
    'A2': 'Professional (Physician) Visit - Home',
    'A3': 'Psychiatric',
    'A4': 'Psychiatric - Room and Board',
    'A5': 'Psychotherapy',
    'A6': 'Psychiatric - Inpatient',
    'A7': 'Psychiatric - Outpatient',
    'A8': 'Rehabilitation',
    'A9': 'Rehabilitation - Room and Board',
    'AA': 'Rehabilitation - Inpatient',
    'AB': 'Rehabilitation - Outpatient',
    'AC': 'Occupational Therapy',
    'AD': 'Physical Medicine',
    'AE': 'Speech Therapy',
    'AF': 'Skilled Nursing Care',
    'AG': 'Skilled Nursing Care - Room and Board',
    'AH': 'Substance Abuse',
    'AI': 'Alcoholism',
    'AJ': 'Drug Addiction',
    'AK': 'Vision (Optometry)',
    'AL': 'Frames',
    'AM': 'Routine Exam',
    'AN': 'Lenses',
    'AO': 'Nonmedically Necessary Physical',
    'AQ': 'Experimental Drug Therapy',
    'AR': 'Burn Care',
    'BA': 'Independent Medical Evaluation',
    'BB': 'Partial Hospitalization (Psychiatric)',
    'BC': 'Day Care (Psychiatric)',
    'BD': 'Cognitive Therapy',
    'BE': 'Massage Therapy',
    'BF': 'Pulmonary Rehabilitation',
    'BG': 'Cardiac Rehabilitation',
    'BH': 'Pediatric',
    'BI': 'Nursery',
    'BJ': 'Skin',
    'BK': 'Orthopedic',
    'BL': 'Cardiac',
    'BM': 'Lymphatic',
    'BN': 'Gastrointestinal',
    'BP': 'Endocrine',
    'BQ': 'Neurology',
    'BR': 'Eye',
    'BS': 'Invasive Procedures',
    'BT': 'Gynecological',
    'BU': 'Obstetrical',
    'BV': 'Obstetrical/Gynecological',
    'BW': 'Mail Order Prescription Drug: Brand Name',
    'BX': 'Mail Order Prescription Drug: Generic',
    'BY': 'Physician Visit - Office: Sick',
    'BZ': 'Physician Visit - Office: Well',
    'C1': 'Coronary Care',
    'CA': 'Private Duty Nursing - Inpatient',
    'CB': 'Private Duty Nursing - Home',
    'CC': 'Surgical Benefits - Professional (Physician)',
    'CD': 'Surgical Benefits - Facility',
    'CE': 'Mental Health Provider - Inpatient',
    'CF': 'Mental Health Provider - Outpatient',
    'CG': 'Mental Health Facility - Inpatient',
    'CH': 'Mental Health Facility - Outpatient',
    'CI': 'Substance Abuse Facility - Inpatient',
    'CJ': 'Substance Abuse Facility - Outpatient',
    'CK': 'Screening X-ray',
    'CL': 'Screening laboratory',
    'CM': 'Mammogram, High Risk Patient',
    'CN': 'Mammogram, Low Risk Patient',
    'CO': 'Flu Vaccination',
    'CP': 'Eyewear and Eyewear Accessories',
    'CQ': 'Case Management',
    'DG': 'Dermatology',
    'DM': 'Durable Medical Equipment',
    'DS': 'Diabetic Supplies',
    'GF': 'Generic Prescription Drug - Formulary',
    'GN': 'Generic Prescription Drug - Non-Formulary',
    'GY': 'Allergy',
    'IC': 'Intensive Care',
    'MH': 'Mental Health',
    'NI': 'Neonatal Intensive Care',
    'ON': 'Oncology',
    'PT': 'Physical Therapy',
    'PU': 'Pulmonary',
    'RN': 'Renal',
    'RT': 'Residential Psychiatric Treatment',
    'TC': 'Transitional Care',
    'TN': 'Transitional Nursery Care',
    'UC': 'Urgent Care'
  };
  
  return serviceTypes[code] || `Service Type ${code}`;
}