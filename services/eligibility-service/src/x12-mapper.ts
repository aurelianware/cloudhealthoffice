/**
 * Cloud Health Office - X12 Eligibility Mapper
 * 
 * Handles conversion between X12 270/271 EDI format and internal types.
 * Based on HIPAA X12 005010X279A1 implementation guide.
 */

import {
  X12_270_Request,
  X12_271_Response,
  EligibilityBenefit
} from './types';

/**
 * X12 service type code to description mapping
 */
const SERVICE_TYPE_DESCRIPTIONS: Record<string, string> = {
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
  '11': 'Used DME',
  '12': 'DME Purchase',
  '14': 'Renal Supplies',
  '15': 'Dialysis',
  '18': 'DME Rental',
  '19': 'Pneumonia Vaccine',
  '20': 'Second Surgical Opinion',
  '21': 'Third Surgical Opinion',
  '22': 'Social Work',
  '23': 'Diagnostic Dental',
  '24': 'Periodontics',
  '25': 'Restorative Dental',
  '26': 'Endodontics',
  '27': 'Maxillofacial Prosthetics',
  '28': 'Adjunctive Dental',
  '30': 'Health Benefit Plan Coverage',
  '32': 'Plan Waiting Period',
  '33': 'Chiropractic',
  '34': 'Chiropractic Office Visits',
  '35': 'Dental Care',
  '36': 'Dental Crowns',
  '37': 'Dental Accident',
  '38': 'Orthodontics',
  '39': 'Prosthodontics',
  '40': 'Oral Surgery',
  '41': 'Routine Preventive Dental',
  '42': 'Home Health Care',
  '43': 'Home Health Prescriptions',
  '44': 'Home Health Visits',
  '45': 'Hospice',
  '46': 'Respite Care',
  '47': 'Hospital',
  '48': 'Hospital Inpatient',
  '49': 'Hospital Outpatient',
  '50': 'Hospital Emergency Accident',
  '51': 'Hospital Emergency Medical',
  '52': 'Hospital Ambulatory Surgical',
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
  '93': 'Podiatry Office Visits',
  '94': 'Podiatry Nursing Home Visits',
  '95': 'Professional Physician',
  '96': 'Anesthesiologist',
  '97': 'Professional Physician Visit Office',
  '98': 'Professional Physician Visit Inpatient',
  '99': 'Professional Physician Visit Outpatient',
  'A0': 'Professional Physician Visit Nursing Home',
  'A1': 'Professional Physician Visit Skilled Nursing',
  'A2': 'Professional Physician Visit Home',
  'A3': 'Psychiatric',
  'A4': 'Psychiatric Room and Board',
  'A5': 'Psychotherapy',
  'A6': 'Psychiatric Inpatient',
  'A7': 'Psychiatric Outpatient',
  'A8': 'Rehabilitation',
  'A9': 'Rehabilitation Room and Board',
  'AA': 'Rehabilitation Inpatient',
  'AB': 'Rehabilitation Outpatient',
  'AC': 'Occupational Therapy',
  'AD': 'Physical Medicine',
  'AE': 'Speech Therapy',
  'AF': 'Skilled Nursing Care',
  'AG': 'Skilled Nursing Room and Board',
  'AH': 'Substance Abuse',
  'AI': 'Alcoholism',
  'AJ': 'Drug Addiction',
  'AK': 'Vision Optometry',
  'AL': 'Frames',
  'AM': 'Routine Exam',
  'AN': 'Lenses',
  'AO': 'Nonmedically Necessary Physical',
  'AQ': 'Experimental Drug Therapy',
  'AR': 'Burn Care',
  'BA': 'Independent Medical Evaluation',
  'BB': 'Partial Hospitalization Psychiatric',
  'BC': 'Day Care Psychiatric',
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
  'BW': 'Mail Order Prescription Drug Brand',
  'BX': 'Mail Order Prescription Drug Generic',
  'BY': 'Physician Visit Office Sick',
  'BZ': 'Physician Visit Office Well',
  'C1': 'Coronary Care',
  'CA': 'Private Duty Nursing Inpatient',
  'CB': 'Private Duty Nursing Home',
  'CC': 'Surgical Benefits Professional',
  'CD': 'Surgical Benefits Facility',
  'CE': 'Mental Health Provider Inpatient',
  'CF': 'Mental Health Provider Outpatient',
  'CG': 'Mental Health Facility Inpatient',
  'CH': 'Mental Health Facility Outpatient',
  'CI': 'Substance Abuse Facility Inpatient',
  'CJ': 'Substance Abuse Facility Outpatient',
  'CK': 'Screening X-Ray',
  'CL': 'Screening Laboratory',
  'CM': 'Mammogram High Risk',
  'CN': 'Mammogram Low Risk',
  'CO': 'Flu Vaccination',
  'CP': 'Eyewear and Accessories',
  'CQ': 'Case Management',
  'DG': 'Dermatology',
  'DM': 'Durable Medical Equipment',
  'DS': 'Diabetic Supplies',
  'GF': 'Generic Prescription Drug Formulary',
  'GN': 'Generic Prescription Drug Non-Formulary',
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

/**
 * Eligibility/Benefit Information Code mapping
 */
const ELIGIBILITY_INFO_CODES: Record<string, string> = {
  '1': 'Active Coverage',
  '2': 'Active - Full Risk Capitation',
  '3': 'Active - Services Capitated',
  '4': 'Active - Services Capitated to Primary Care Physician',
  '5': 'Active - Pending Investigation',
  '6': 'Inactive',
  '7': 'Inactive - Pending Eligibility Update',
  '8': 'Inactive - Pending Investigation',
  'A': 'Co-Insurance',
  'B': 'Co-Payment',
  'C': 'Deductible',
  'CB': 'Coverage Basis',
  'D': 'Benefit Description',
  'E': 'Exclusions',
  'F': 'Limitations',
  'G': 'Out of Pocket (Stop Loss)',
  'H': 'Unlimited',
  'I': 'Non-Covered',
  'J': 'Cost Containment',
  'K': 'Reserve',
  'L': 'Primary Care Provider',
  'M': 'Pre-existing Condition',
  'MC': 'Managed Care Coordinator',
  'N': 'Services Restricted to Following Provider',
  'O': 'Not Deemed a Medical Necessity',
  'P': 'Benefit Disclaimer',
  'Q': 'Second Surgical Opinion Required',
  'R': 'Other or Additional Payor',
  'S': 'Prior Year(s) History',
  'T': 'Card(s) Reported Lost/Stolen',
  'U': 'Contact Following Entity for Eligibility or Benefit Information',
  'V': 'Cannot Process',
  'W': 'Other Source of Data',
  'X': 'Health Care Facility',
  'Y': 'Spend Down'
};

/**
 * X12 Eligibility Mapper class
 */
export class X12EligibilityMapper {
  
  /**
   * Parse raw X12 270 EDI string to structured request
   */
  parseX12270(ediString: string): X12_270_Request {
    const segments = this.parseSegments(ediString);
    
    // Extract ISA segment
    const isa = segments.find(s => s.id === 'ISA');
    const interchangeControlNumber = isa?.elements[12] || '';
    
    // Extract transaction date from BHT segment
    const bht = segments.find(s => s.id === 'BHT');
    const transactionControlNumber = bht?.elements[2] || '';
    const transactionDate = bht?.elements[3] || '';
    const transactionTime = bht?.elements[4];
    
    // Extract information source (2100A loop - payer)
    const nm1Source = this.findLoopNM1(segments, '2B', 'PR');
    const informationSource = {
      entityIdentifier: nm1Source?.elements[0] || '',
      entityType: (nm1Source?.elements[1] || '2') as '1' | '2',
      name: nm1Source?.elements[2] || '',
      identificationCode: nm1Source?.elements[8] || '',
      identificationCodeQualifier: nm1Source?.elements[7] || ''
    };
    
    // Extract information receiver (2100B loop - provider)
    const nm1Receiver = this.findLoopNM1(segments, '1P', 'FA');
    const informationReceiver = {
      entityIdentifier: nm1Receiver?.elements[0] || '',
      entityType: (nm1Receiver?.elements[1] || '1') as '1' | '2',
      name: nm1Receiver?.elements[2],
      npi: nm1Receiver?.elements[8],
      identificationCode: nm1Receiver?.elements[8],
      identificationCodeQualifier: nm1Receiver?.elements[7]
    };
    
    // Extract subscriber (2100C loop)
    const nm1Subscriber = this.findLoopNM1(segments, 'IL');
    const dmgSubscriber = this.findSegmentAfterNM1(segments, nm1Subscriber, 'DMG');
    const n3Subscriber = this.findSegmentAfterNM1(segments, nm1Subscriber, 'N3');
    const n4Subscriber = this.findSegmentAfterNM1(segments, nm1Subscriber, 'N4');
    const refSubscriber = this.findSegmentAfterNM1(segments, nm1Subscriber, 'REF');
    
    const subscriber = {
      memberId: refSubscriber?.elements[1] || nm1Subscriber?.elements[8] || '',
      firstName: nm1Subscriber?.elements[3] || '',
      lastName: nm1Subscriber?.elements[2] || '',
      middleName: nm1Subscriber?.elements[4],
      suffix: nm1Subscriber?.elements[6],
      dateOfBirth: dmgSubscriber?.elements[1] || '',
      gender: dmgSubscriber?.elements[2] as 'M' | 'F' | 'U' | undefined,
      ssn: this.findRefValue(segments, 'SY'),
      groupNumber: this.findRefValue(segments, '6P'),
      address: n3Subscriber ? {
        line1: n3Subscriber.elements[0],
        line2: n3Subscriber.elements[1],
        city: n4Subscriber?.elements[0],
        state: n4Subscriber?.elements[1],
        postalCode: n4Subscriber?.elements[2],
        countryCode: n4Subscriber?.elements[3]
      } : undefined
    };
    
    // Extract dependent (2100D loop) if present
    const nm1Dependent = this.findLoopNM1(segments, '03');
    let dependent;
    if (nm1Dependent) {
      const dmgDependent = this.findSegmentAfterNM1(segments, nm1Dependent, 'DMG');
      const insDependent = this.findSegmentBefore(segments, nm1Dependent, 'INS');
      
      dependent = {
        firstName: nm1Dependent.elements[3] || '',
        lastName: nm1Dependent.elements[2] || '',
        middleName: nm1Dependent.elements[4],
        suffix: nm1Dependent.elements[6],
        dateOfBirth: dmgDependent?.elements[1] || '',
        gender: dmgDependent?.elements[2] as 'M' | 'F' | 'U' | undefined,
        relationshipCode: insDependent?.elements[1] || '19'
      };
    }
    
    // Extract service types (EQ segments)
    const eqSegments = segments.filter(s => s.id === 'EQ');
    const serviceTypeCodes = eqSegments
      .map(eq => eq.elements[0])
      .filter(Boolean);
    
    // Extract date range (DTP segment)
    const dtpSegment = segments.find(s => s.id === 'DTP' && s.elements[0] === '291');
    let eligibilityDateRange;
    if (dtpSegment) {
      const dateValue = dtpSegment.elements[2] || '';
      if (dateValue.includes('-')) {
        const [start, end] = dateValue.split('-');
        eligibilityDateRange = { startDate: start, endDate: end };
      } else {
        eligibilityDateRange = { startDate: dateValue };
      }
    }
    
    return {
      transactionControlNumber,
      interchangeControlNumber,
      transactionDate,
      transactionTime,
      informationSource,
      informationReceiver,
      subscriber,
      dependent,
      eligibilityDateRange,
      serviceTypeCodes: serviceTypeCodes.length > 0 ? serviceTypeCodes : undefined
    };
  }
  
  /**
   * Generate X12 271 EDI string from structured response
   */
  generateX12271(response: X12_271_Response): string {
    const segments: string[] = [];
    const elementSeparator = '*';
    const segmentSeparator = '~';
    const date = response.transactionDate;
    const time = response.transactionTime || '1200';
    
    // ISA - Interchange Control Header
    segments.push(`ISA*00*          *00*          *ZZ*${this.pad(response.informationSource.identificationCode, 15)}*ZZ*${this.pad(response.informationReceiver?.npi || '', 15)}*${date.substring(2, 6)}*${time.substring(0, 4)}*^*00501*${this.pad(response.responseControlNumber, 9)}*0*P*:`);
    
    // GS - Functional Group Header
    segments.push(`GS*HB*${response.informationSource.identificationCode}*${response.informationReceiver?.npi || ''}*${date}*${time}*1*X*005010X279A1`);
    
    // ST - Transaction Set Header
    segments.push(`ST*271*0001*005010X279A1`);
    
    // BHT - Beginning of Hierarchical Transaction
    segments.push(`BHT*0022*11*${response.responseControlNumber}*${date}*${time}`);
    
    // 2000A - Information Source Level
    segments.push(`HL*1**20*1`);
    
    // 2100A - Information Source Name
    segments.push(`NM1*PR*2*${response.informationSource.name}*****PI*${response.informationSource.identificationCode}`);
    
    // 2000B - Information Receiver Level
    segments.push(`HL*2*1*21*1`);
    
    // 2100B - Information Receiver Name
    if (response.informationReceiver?.npi) {
      segments.push(`NM1*1P*2*${response.informationReceiver.name || ''}*****XX*${response.informationReceiver.npi}`);
    }
    
    // 2000C - Subscriber Level
    segments.push(`HL*3*2*22*${response.dependent ? '1' : '0'}`);
    
    // TRN - Trace
    segments.push(`TRN*2*${response.transactionControlNumber}*${response.informationSource.identificationCode}`);
    
    // 2100C - Subscriber Name
    const sub = response.subscriber;
    segments.push(`NM1*IL*1*${sub.lastName}*${sub.firstName}****MI*${sub.memberId}`);
    
    // DMG - Subscriber Demographics
    if (sub.dateOfBirth) {
      segments.push(`DMG*D8*${sub.dateOfBirth}*${sub.gender || 'U'}`);
    }
    
    // 2110C - Subscriber Eligibility or Benefit Information
    for (const benefit of response.benefits) {
      segments.push(this.generateEBSegment(benefit));
    }
    
    // 2000D - Dependent Level (if applicable)
    if (response.dependent) {
      segments.push(`HL*4*3*23*0`);
      
      // 2100D - Dependent Name
      const dep = response.dependent;
      segments.push(`NM1*03*1*${dep.lastName}*${dep.firstName}****MI*${sub.memberId}`);
      
      // DMG - Dependent Demographics
      if (dep.dateOfBirth) {
        segments.push(`DMG*D8*${dep.dateOfBirth}*U`);
      }
      
      // 2110D - Dependent Eligibility or Benefit Information
      for (const benefit of response.benefits) {
        segments.push(this.generateEBSegment(benefit));
      }
    }
    
    // SE - Transaction Set Trailer
    const segmentCount = segments.length - 2; // Exclude ISA and GS
    segments.push(`SE*${segmentCount + 1}*0001`);
    
    // GE - Functional Group Trailer
    segments.push(`GE*1*1`);
    
    // IEA - Interchange Control Trailer
    segments.push(`IEA*1*${this.pad(response.responseControlNumber, 9)}`);
    
    return segments.join(segmentSeparator) + segmentSeparator;
  }
  
  /**
   * Get service type description
   */
  getServiceTypeDescription(code: string): string {
    return SERVICE_TYPE_DESCRIPTIONS[code] || `Service Type ${code}`;
  }
  
  /**
   * Get eligibility info code description
   */
  getEligibilityInfoDescription(code: string): string {
    return ELIGIBILITY_INFO_CODES[code] || `Code ${code}`;
  }
  
  // ============================================================================
  // Private Helper Methods
  // ============================================================================
  
  private parseSegments(ediString: string): Array<{ id: string; elements: string[] }> {
    // Detect delimiters from ISA segment
    const elementSeparator = ediString.charAt(3);
    const segmentSeparator = ediString.charAt(105) || '~';
    
    const segmentStrings = ediString.split(segmentSeparator).filter(s => s.trim());
    
    return segmentStrings.map(segStr => {
      const elements = segStr.split(elementSeparator);
      return {
        id: elements[0].trim(),
        elements: elements.slice(1)
      };
    });
  }
  
  private findLoopNM1(segments: Array<{ id: string; elements: string[] }>, ...codes: string[]): { id: string; elements: string[] } | undefined {
    return segments.find(s => 
      s.id === 'NM1' && codes.some(code => s.elements[0] === code)
    );
  }
  
  private findSegmentAfterNM1(
    segments: Array<{ id: string; elements: string[] }>, 
    nm1: { id: string; elements: string[] } | undefined,
    segmentId: string
  ): { id: string; elements: string[] } | undefined {
    if (!nm1) return undefined;
    const nm1Index = segments.indexOf(nm1);
    if (nm1Index === -1) return undefined;
    
    // Look for the segment within the next few segments (within the same loop)
    for (let i = nm1Index + 1; i < Math.min(nm1Index + 10, segments.length); i++) {
      if (segments[i].id === segmentId) {
        return segments[i];
      }
      // Stop if we hit another NM1 or HL (start of new loop)
      if (segments[i].id === 'NM1' || segments[i].id === 'HL') {
        break;
      }
    }
    return undefined;
  }
  
  private findSegmentBefore(
    segments: Array<{ id: string; elements: string[] }>,
    nm1: { id: string; elements: string[] } | undefined,
    segmentId: string
  ): { id: string; elements: string[] } | undefined {
    if (!nm1) return undefined;
    const nm1Index = segments.indexOf(nm1);
    if (nm1Index === -1) return undefined;
    
    // Look backwards for the segment
    for (let i = nm1Index - 1; i >= Math.max(0, nm1Index - 5); i--) {
      if (segments[i].id === segmentId) {
        return segments[i];
      }
    }
    return undefined;
  }
  
  private findRefValue(segments: Array<{ id: string; elements: string[] }>, qualifier: string): string | undefined {
    const ref = segments.find(s => s.id === 'REF' && s.elements[0] === qualifier);
    return ref?.elements[1];
  }
  
  private generateEBSegment(benefit: EligibilityBenefit): string {
    const elements = [
      benefit.eligibilityInfoCode,
      benefit.coverageLevelCode || '',
      benefit.serviceTypeCode,
      benefit.insuranceTypeCode || '',
      benefit.planCoverageDescription || '',
      benefit.timePeriodQualifier || '',
      benefit.benefitAmount?.toString() || '',
      benefit.benefitPercent?.toString() || '',
      benefit.quantityQualifier || '',
      benefit.quantity?.toString() || '',
      benefit.authorizationRequired ? 'Y' : 'N'
    ];
    
    // Trim trailing empty elements
    while (elements.length > 0 && elements[elements.length - 1] === '') {
      elements.pop();
    }
    
    return `EB*${elements.join('*')}`;
  }
  
  private pad(str: string, length: number, char: string = ' '): string {
    return str.padEnd(length, char).substring(0, length);
  }
}
