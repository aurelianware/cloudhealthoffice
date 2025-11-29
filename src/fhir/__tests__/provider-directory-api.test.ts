/**
 * Provider Directory API Test Suite
 * 
 * Tests for NPPES integration and FHIR resource mapping:
 * - Practitioner mapping from NPI-1
 * - Organization mapping from NPI-2
 * - PractitionerRole generation
 * - Location mapping
 * - NPI validation
 * - Search operations
 */

import { describe, it, expect, beforeEach } from '@jest/globals';
import {
  ProviderDirectoryApi,
  createProviderDirectoryApi,
  NPPESResult,
  NPPESBasicInfo,
  NPPESAddress,
  NPPESTaxonomy
} from '../provider-directory-api';

// Mock NPPES data for testing
const mockPractitionerNPPES: NPPESResult = {
  number: '1234567890',
  enumeration_type: 'NPI-1',
  basic: {
    first_name: 'Jane',
    last_name: 'Smith',
    middle_name: 'Marie',
    name_prefix: 'Dr.',
    name_suffix: 'MD',
    credential: 'MD',
    gender: 'F',
    enumeration_date: '2010-01-15',
    last_updated: '2024-01-01',
    status: 'A'
  },
  addresses: [
    {
      address_purpose: 'LOCATION',
      address_1: '123 Medical Center Drive',
      address_2: 'Suite 400',
      city: 'Boston',
      state: 'MA',
      postal_code: '02101',
      country_code: 'US',
      country_name: 'United States',
      telephone_number: '6175551234',
      fax_number: '6175551235'
    },
    {
      address_purpose: 'MAILING',
      address_1: 'PO Box 1234',
      city: 'Boston',
      state: 'MA',
      postal_code: '02102',
      country_code: 'US',
      country_name: 'United States'
    }
  ],
  taxonomies: [
    {
      code: '207R00000X',
      desc: 'Internal Medicine',
      primary: true,
      state: 'MA',
      license: 'MA12345'
    },
    {
      code: '207RC0000X',
      desc: 'Cardiovascular Disease',
      primary: false,
      state: 'MA',
      license: 'MA12346'
    }
  ]
};

const mockOrganizationNPPES: NPPESResult = {
  number: '9876543210',
  enumeration_type: 'NPI-2',
  basic: {
    organization_name: 'Boston Medical Center',
    organizational_subpart: 'Cardiology Department',
    enumeration_date: '2005-06-01',
    last_updated: '2024-02-15',
    status: 'A',
    authorized_official_first_name: 'John',
    authorized_official_last_name: 'Director',
    authorized_official_title_or_position: 'CEO'
  },
  addresses: [
    {
      address_purpose: 'LOCATION',
      address_1: '1 Medical Center Plaza',
      city: 'Boston',
      state: 'MA',
      postal_code: '02118',
      country_code: 'US',
      country_name: 'United States',
      telephone_number: '6176381000',
      fax_number: '6176381001'
    }
  ],
  taxonomies: [
    {
      code: '282N00000X',
      desc: 'General Acute Care Hospital',
      primary: true
    }
  ],
  other_names: [
    {
      organization_name: 'BMC',
      type: 'DBA'
    }
  ]
};

describe('Provider Directory API', () => {
  let api: ProviderDirectoryApi;

  beforeEach(() => {
    api = createProviderDirectoryApi();
  });

  describe('NPI Validation', () => {
    it('should validate a correct NPI number', () => {
      // Valid NPIs pass Luhn check with 80840 prefix
      expect(api.validateNPI('1234567893')).toBe(true);
    });

    it('should reject NPI with wrong length', () => {
      expect(api.validateNPI('123456789')).toBe(false);  // 9 digits
      expect(api.validateNPI('12345678901')).toBe(false); // 11 digits
    });

    it('should reject NPI with non-numeric characters', () => {
      expect(api.validateNPI('123456789A')).toBe(false);
      expect(api.validateNPI('123-456-78')).toBe(false);
    });

    it('should reject NPI that fails Luhn check', () => {
      expect(api.validateNPI('1234567891')).toBe(false);
    });
  });

  describe('NPPES to Practitioner Mapping', () => {
    it('should map NPI-1 to Practitioner resource', () => {
      const practitioner = api.mapNPPESToPractitioner(mockPractitionerNPPES);

      expect(practitioner.resourceType).toBe('Practitioner');
      expect(practitioner.id).toBe('1234567890');
      expect(practitioner.active).toBe(true);
    });

    it('should include US Core profile in meta', () => {
      const practitioner = api.mapNPPESToPractitioner(mockPractitionerNPPES);

      expect(practitioner.meta?.profile).toContain(
        'http://hl7.org/fhir/us/core/StructureDefinition/us-core-practitioner'
      );
    });

    it('should map NPI identifier correctly', () => {
      const practitioner = api.mapNPPESToPractitioner(mockPractitionerNPPES);

      const npiIdentifier = practitioner.identifier?.find(
        id => id.system === 'http://hl7.org/fhir/sid/us-npi'
      );
      expect(npiIdentifier).toBeDefined();
      expect(npiIdentifier?.value).toBe('1234567890');
    });

    it('should map name with prefix and suffix', () => {
      const practitioner = api.mapNPPESToPractitioner(mockPractitionerNPPES);

      const name = practitioner.name?.[0];
      expect(name?.family).toBe('Smith');
      expect(name?.given).toContain('Jane');
      expect(name?.given).toContain('Marie');
      expect(name?.prefix).toContain('Dr.');
      expect(name?.suffix).toContain('MD');
    });

    it('should map gender correctly', () => {
      const practitioner = api.mapNPPESToPractitioner(mockPractitionerNPPES);
      expect(practitioner.gender).toBe('female');
    });

    it('should map addresses', () => {
      const practitioner = api.mapNPPESToPractitioner(mockPractitionerNPPES);

      expect(practitioner.address).toHaveLength(2);
      
      const locationAddr = practitioner.address?.find(a => a.use === 'work');
      expect(locationAddr?.line).toContain('123 Medical Center Drive');
      expect(locationAddr?.city).toBe('Boston');
      expect(locationAddr?.state).toBe('MA');
      expect(locationAddr?.postalCode).toBe('02101');
    });

    it('should map telecom (phone and fax)', () => {
      const practitioner = api.mapNPPESToPractitioner(mockPractitionerNPPES);

      const phone = practitioner.telecom?.find(t => t.system === 'phone');
      expect(phone).toBeDefined();
      expect(phone?.value).toContain('617');

      const fax = practitioner.telecom?.find(t => t.system === 'fax');
      expect(fax).toBeDefined();
    });

    it('should map qualifications from taxonomies', () => {
      const practitioner = api.mapNPPESToPractitioner(mockPractitionerNPPES);

      expect(practitioner.qualification).toHaveLength(2);
      
      const primaryQual = practitioner.qualification?.[0];
      expect(primaryQual?.code?.coding?.[0].code).toBe('207R00000X');
      expect(primaryQual?.code?.coding?.[0].display).toBe('Internal Medicine');
    });

    it('should throw error for NPI-2 (organization)', () => {
      expect(() => {
        api.mapNPPESToPractitioner(mockOrganizationNPPES);
      }).toThrow(/Cannot map NPI-2/);
    });

    it('should mark practitioner as inactive if deactivated', () => {
      const deactivatedNPPES: NPPESResult = {
        ...mockPractitionerNPPES,
        basic: {
          ...mockPractitionerNPPES.basic,
          deactivation_date: '2023-01-01'
        }
      };

      const practitioner = api.mapNPPESToPractitioner(deactivatedNPPES);
      expect(practitioner.active).toBe(false);
    });
  });

  describe('NPPES to Organization Mapping', () => {
    it('should map NPI-2 to Organization resource', () => {
      const organization = api.mapNPPESToOrganization(mockOrganizationNPPES);

      expect(organization.resourceType).toBe('Organization');
      expect(organization.id).toBe('9876543210');
      expect(organization.active).toBe(true);
    });

    it('should include US Core profile in meta', () => {
      const organization = api.mapNPPESToOrganization(mockOrganizationNPPES);

      expect(organization.meta?.profile).toContain(
        'http://hl7.org/fhir/us/core/StructureDefinition/us-core-organization'
      );
    });

    it('should map organization name', () => {
      const organization = api.mapNPPESToOrganization(mockOrganizationNPPES);
      expect(organization.name).toBe('Boston Medical Center');
    });

    it('should include alias (other names)', () => {
      const organization = api.mapNPPESToOrganization(mockOrganizationNPPES);
      expect(organization.alias).toContain('BMC');
    });

    it('should map organization type from taxonomy', () => {
      const organization = api.mapNPPESToOrganization(mockOrganizationNPPES);

      expect(organization.type).toHaveLength(1);
      expect(organization.type?.[0].coding?.[0].code).toBe('282N00000X');
      expect(organization.type?.[0].coding?.[0].display).toBe('General Acute Care Hospital');
    });

    it('should throw error for NPI-1 (individual)', () => {
      expect(() => {
        api.mapNPPESToOrganization(mockPractitionerNPPES);
      }).toThrow(/Cannot map NPI-1/);
    });
  });

  describe('NPPES to PractitionerRole Mapping', () => {
    it('should create PractitionerRole from practitioner NPPES', () => {
      const role = api.mapNPPESToPractitionerRole(mockPractitionerNPPES);

      expect(role.resourceType).toBe('PractitionerRole');
      expect(role.id).toBe('1234567890-role');
    });

    it('should include US Core profile in meta', () => {
      const role = api.mapNPPESToPractitionerRole(mockPractitionerNPPES);

      expect(role.meta?.profile).toContain(
        'http://hl7.org/fhir/us/core/StructureDefinition/us-core-practitionerrole'
      );
    });

    it('should reference practitioner', () => {
      const role = api.mapNPPESToPractitionerRole(mockPractitionerNPPES);

      expect(role.practitioner?.reference).toBe('Practitioner/1234567890');
    });

    it('should include organization reference when provided', () => {
      const orgRef = { reference: 'Organization/9876543210' };
      const role = api.mapNPPESToPractitionerRole(mockPractitionerNPPES, orgRef);

      expect(role.organization).toBe(orgRef);
    });

    it('should map specialties from taxonomies', () => {
      const role = api.mapNPPESToPractitionerRole(mockPractitionerNPPES);

      expect(role.specialty).toHaveLength(2);
      expect(role.specialty?.[0].coding?.[0].code).toBe('207R00000X');
    });

    it('should map role code from primary taxonomy', () => {
      const role = api.mapNPPESToPractitionerRole(mockPractitionerNPPES);

      expect(role.code).toHaveLength(1);
      expect(role.code?.[0].coding?.[0].display).toBe('Internal Medicine');
    });

    it('should include location references', () => {
      const role = api.mapNPPESToPractitionerRole(mockPractitionerNPPES);

      expect(role.location).toHaveLength(1);
      expect(role.location?.[0].reference).toBe('Location/1234567890-loc-0');
    });
  });

  describe('NPPES to Location Mapping', () => {
    it('should create Location from NPPES address', () => {
      const location = api.mapNPPESToLocation(mockPractitionerNPPES, 0);

      expect(location.resourceType).toBe('Location');
      expect(location.id).toBe('1234567890-loc-0');
    });

    it('should include US Core profile in meta', () => {
      const location = api.mapNPPESToLocation(mockPractitionerNPPES, 0);

      expect(location.meta?.profile).toContain(
        'http://hl7.org/fhir/us/core/StructureDefinition/us-core-location'
      );
    });

    it('should map address correctly', () => {
      const location = api.mapNPPESToLocation(mockPractitionerNPPES, 0);

      expect(location.address?.line).toContain('123 Medical Center Drive');
      expect(location.address?.city).toBe('Boston');
      expect(location.address?.state).toBe('MA');
    });

    it('should set status based on enumeration status', () => {
      const location = api.mapNPPESToLocation(mockPractitionerNPPES, 0);
      expect(location.status).toBe('active');

      const deactivatedNPPES: NPPESResult = {
        ...mockPractitionerNPPES,
        basic: {
          ...mockPractitionerNPPES.basic,
          deactivation_date: '2023-01-01'
        }
      };
      const inactiveLocation = api.mapNPPESToLocation(deactivatedNPPES, 0);
      expect(inactiveLocation.status).toBe('inactive');
    });

    it('should include telecom from address', () => {
      const location = api.mapNPPESToLocation(mockPractitionerNPPES, 0);

      expect(location.telecom).toBeDefined();
      const phone = location.telecom?.find(t => t.system === 'phone');
      expect(phone).toBeDefined();
    });

    it('should include managing organization for NPI-2', () => {
      const location = api.mapNPPESToLocation(mockOrganizationNPPES, 0);

      expect(location.managingOrganization?.reference).toBe('Organization/9876543210');
    });

    it('should throw error if address index out of range', () => {
      // Create NPPES data with only MAILING addresses (no LOCATION)
      const nppesWithMailingOnly: NPPESResult = {
        ...mockPractitionerNPPES,
        addresses: [
          {
            address_purpose: 'MAILING',
            address_1: 'PO Box 1234',
            city: 'Boston',
            state: 'MA',
            postal_code: '02102',
            country_code: 'US',
            country_name: 'United States'
          }
        ]
      };
      
      // Index 10 is out of range for an array with 1 element
      expect(() => {
        api.mapNPPESToLocation(nppesWithMailingOnly, 10);
      }).toThrow(/No address found.*at index 10/);
    });
  });

  describe('Search Bundle Creation', () => {
    it('should create valid FHIR searchset Bundle', () => {
      const practitioner = api.mapNPPESToPractitioner(mockPractitionerNPPES);
      // Access private method via type assertion for testing
      const bundle = (api as any).createSearchBundle('Practitioner', [practitioner]);

      expect(bundle.resourceType).toBe('Bundle');
      expect(bundle.type).toBe('searchset');
      expect(bundle.total).toBe(1);
      expect(bundle.entry).toHaveLength(1);
      expect(bundle.entry[0].search?.mode).toBe('match');
    });

    it('should handle empty results', () => {
      const bundle = (api as any).createSearchBundle('Practitioner', []);

      expect(bundle.total).toBe(0);
      expect(bundle.entry).toHaveLength(0);
    });
  });
});

describe('Provider Directory API Factory', () => {
  it('should create API instance with default NPPES URL', () => {
    const api = createProviderDirectoryApi();
    expect(api).toBeInstanceOf(ProviderDirectoryApi);
  });

  it('should create API instance with custom NPPES URL', () => {
    const api = createProviderDirectoryApi('https://custom.nppes.api/v2');
    expect(api).toBeInstanceOf(ProviderDirectoryApi);
  });

  it('should create API instance with Cosmos config', () => {
    const api = createProviderDirectoryApi(
      undefined,
      'https://myaccount.documents.azure.com:443/',
      'mydb',
      'providers'
    );
    expect(api).toBeInstanceOf(ProviderDirectoryApi);
  });
});
