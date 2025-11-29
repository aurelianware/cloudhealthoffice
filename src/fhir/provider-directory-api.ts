/**
 * Provider Directory API - CMS-0057-F Final Rule Implementation
 * 
 * Implements Provider Directory API for exposing FHIR R4 provider resources:
 * - Practitioner (individual healthcare providers)
 * - PractitionerRole (provider roles/specialties at organizations)
 * - Organization (healthcare organizations)
 * - Location (physical service delivery locations)
 * 
 * Integrates with NPPES (National Plan and Provider Enumeration System)
 * for NPI validation and provider data enrichment.
 * 
 * References:
 * - CMS-0057-F Final Rule (Provider Directory Requirements)
 * - HL7 Da Vinci Payer Data Exchange (PDex) Implementation Guide
 * - US Core Implementation Guide v3.1.1+
 * - FHIR R4.0.1 Specification
 * - NPPES NPI Registry API
 * 
 * @module provider-directory-api
 */

import {
  Practitioner,
  PractitionerRole,
  Organization,
  Location,
  Bundle,
  Reference,
  Address,
  CodeableConcept,
  ContactPoint,
  HumanName,
  Identifier
} from 'fhir/r4';

// ============================================================================
// NPPES Integration Types
// ============================================================================

/**
 * NPPES API Response structure
 * Based on https://npiregistry.cms.hhs.gov/api-page
 */
export interface NPPESResponse {
  result_count: number;
  results: NPPESResult[];
}

/**
 * Individual NPPES result record
 */
export interface NPPESResult {
  /** NPI number (10 digits) */
  number: string;
  /** Enumeration type: NPI-1 (individual) or NPI-2 (organization) */
  enumeration_type: 'NPI-1' | 'NPI-2';
  /** Basic provider information */
  basic: NPPESBasicInfo;
  /** Practice addresses */
  addresses: NPPESAddress[];
  /** Taxonomies (specialties) */
  taxonomies: NPPESTaxonomy[];
  /** Other identifiers */
  identifiers?: NPPESIdentifier[];
  /** Other names */
  other_names?: NPPESOtherName[];
}

/**
 * NPPES basic provider information
 */
export interface NPPESBasicInfo {
  /** Provider first name (NPI-1) */
  first_name?: string;
  /** Provider last name (NPI-1) */
  last_name?: string;
  /** Provider middle name (NPI-1) */
  middle_name?: string;
  /** Name prefix (Dr., etc.) */
  name_prefix?: string;
  /** Name suffix (MD, DO, etc.) */
  name_suffix?: string;
  /** Credential (MD, DO, NP, etc.) */
  credential?: string;
  /** Organization name (NPI-2) */
  organization_name?: string;
  /** Organizational subpart */
  organizational_subpart?: string;
  /** Authorized official first name */
  authorized_official_first_name?: string;
  /** Authorized official last name */
  authorized_official_last_name?: string;
  /** Authorized official title */
  authorized_official_title_or_position?: string;
  /** Gender (M/F) */
  gender?: string;
  /** Enumeration date (YYYY-MM-DD) */
  enumeration_date?: string;
  /** Last updated date */
  last_updated?: string;
  /** NPI deactivation date (if deactivated) */
  deactivation_date?: string;
  /** NPI reactivation date (if reactivated) */
  reactivation_date?: string;
  /** Status (A=Active) */
  status?: string;
  /** Sole proprietor flag */
  sole_proprietor?: string;
}

/**
 * NPPES address information
 */
export interface NPPESAddress {
  /** Address purpose: LOCATION or MAILING */
  address_purpose: 'LOCATION' | 'MAILING';
  /** Street address line 1 */
  address_1: string;
  /** Street address line 2 */
  address_2?: string;
  /** City */
  city: string;
  /** State (2-letter code) */
  state: string;
  /** Postal code */
  postal_code: string;
  /** Country code */
  country_code: string;
  /** Country name */
  country_name: string;
  /** Telephone number */
  telephone_number?: string;
  /** Fax number */
  fax_number?: string;
}

/**
 * NPPES taxonomy (specialty) information
 */
export interface NPPESTaxonomy {
  /** Taxonomy code */
  code: string;
  /** Taxonomy description */
  desc: string;
  /** Primary taxonomy flag (Y/N) */
  primary: boolean;
  /** State where licensed */
  state?: string;
  /** License number */
  license?: string;
}

/**
 * NPPES other identifier
 */
export interface NPPESIdentifier {
  /** Identifier code */
  code?: string;
  /** Identifier description */
  desc?: string;
  /** Identifier issuer */
  issuer?: string;
  /** Identifier value */
  identifier?: string;
  /** State */
  state?: string;
}

/**
 * NPPES other name
 */
export interface NPPESOtherName {
  /** Organization name */
  organization_name?: string;
  /** Name type code */
  code?: string;
  /** Name type description */
  type?: string;
}

// ============================================================================
// Search Parameters
// ============================================================================

/**
 * Search parameters for Practitioner resources
 */
export interface PractitionerSearchParams {
  /** NPI number */
  npi?: string;
  /** Provider name (partial match) */
  name?: string;
  /** Provider family name */
  family?: string;
  /** Provider given name */
  given?: string;
  /** City */
  city?: string;
  /** State (2-letter code) */
  state?: string;
  /** Postal code */
  postalCode?: string;
  /** Specialty/taxonomy code */
  specialty?: string;
  /** Active status */
  active?: boolean;
  /** Page number */
  _page?: number;
  /** Results per page (max 200) */
  _count?: number;
}

/**
 * Search parameters for Organization resources
 */
export interface OrganizationSearchParams {
  /** NPI number */
  npi?: string;
  /** Organization name (partial match) */
  name?: string;
  /** City */
  city?: string;
  /** State (2-letter code) */
  state?: string;
  /** Postal code */
  postalCode?: string;
  /** Organization type */
  type?: string;
  /** Active status */
  active?: boolean;
  /** Page number */
  _page?: number;
  /** Results per page (max 200) */
  _count?: number;
}

/**
 * Search parameters for Location resources
 */
export interface LocationSearchParams {
  /** Organization NPI */
  organization?: string;
  /** Location name */
  name?: string;
  /** City */
  city?: string;
  /** State (2-letter code) */
  state?: string;
  /** Postal code */
  postalCode?: string;
  /** Location type */
  type?: string;
  /** Active status */
  status?: 'active' | 'suspended' | 'inactive';
  /** Page number */
  _page?: number;
  /** Results per page (max 200) */
  _count?: number;
}

// ============================================================================
// Provider Directory API Class
// ============================================================================

/**
 * Provider Directory API
 * 
 * Exposes FHIR R4 provider directory resources with NPPES integration
 */
export class ProviderDirectoryApi {
  /**
   * NPI Luhn validation prefix - CMS-assigned prefix for NPI check digit calculation
   * Per CMS specification: https://www.cms.gov/Regulations-and-Guidance/Administrative-Simplification/NationalProvIdentStand/Downloads/NPIcheckdigit.pdf
   */
  private static readonly NPI_LUHN_PREFIX = '80840';

  private nppesBaseUrl: string;
  private cosmosEndpoint: string;
  private cosmosDatabase: string;
  private cosmosContainer: string;

  /**
   * Initialize Provider Directory API
   * @param nppesBaseUrl - NPPES API base URL (default: https://npiregistry.cms.hhs.gov/api)
   * @param cosmosEndpoint - Cosmos DB endpoint for caching
   * @param cosmosDatabase - Cosmos DB database name
   * @param cosmosContainer - Cosmos DB container name for provider directory
   */
  constructor(
    nppesBaseUrl: string = 'https://npiregistry.cms.hhs.gov/api',
    cosmosEndpoint?: string,
    cosmosDatabase: string = 'cloudhealthoffice',
    cosmosContainer: string = 'ProviderDirectory'
  ) {
    this.nppesBaseUrl = nppesBaseUrl;
    this.cosmosEndpoint = cosmosEndpoint || '';
    this.cosmosDatabase = cosmosDatabase;
    this.cosmosContainer = cosmosContainer;
  }

  // ==========================================================================
  // NPPES Integration
  // ==========================================================================

  /**
   * Lookup provider in NPPES registry by NPI
   * @param npi - 10-digit NPI number
   * @returns NPPES result or null if not found
   */
  async lookupNPPES(npi: string): Promise<NPPESResult | null> {
    if (!this.validateNPI(npi)) {
      throw new Error(`Invalid NPI format: ${npi}. NPI must be a 10-digit number.`);
    }

    const url = `${this.nppesBaseUrl}/?version=2.1&number=${npi}`;
    
    try {
      const response = await fetch(url);
      
      if (!response.ok) {
        throw new Error(`NPPES API error: ${response.status} ${response.statusText}`);
      }

      const data = await response.json() as NPPESResponse;
      
      if (data.result_count === 0 || !data.results || data.results.length === 0) {
        return null;
      }

      return data.results[0];
    } catch (error) {
      console.error(`[ProviderDirectoryApi] NPPES lookup failed for NPI ${npi}:`, error);
      throw error;
    }
  }

  /**
   * Search NPPES registry with multiple parameters
   * @param params - Search parameters
   * @returns Array of NPPES results
   */
  async searchNPPES(params: {
    first_name?: string;
    last_name?: string;
    organization_name?: string;
    city?: string;
    state?: string;
    postal_code?: string;
    taxonomy_description?: string;
    enumeration_type?: 'NPI-1' | 'NPI-2';
    limit?: number;
    skip?: number;
  }): Promise<NPPESResult[]> {
    const queryParams = new URLSearchParams({ version: '2.1' });
    
    if (params.first_name) queryParams.append('first_name', params.first_name);
    if (params.last_name) queryParams.append('last_name', params.last_name);
    if (params.organization_name) queryParams.append('organization_name', params.organization_name);
    if (params.city) queryParams.append('city', params.city);
    if (params.state) queryParams.append('state', params.state);
    if (params.postal_code) queryParams.append('postal_code', params.postal_code);
    if (params.taxonomy_description) queryParams.append('taxonomy_description', params.taxonomy_description);
    if (params.enumeration_type) queryParams.append('enumeration_type', params.enumeration_type);
    if (params.limit) queryParams.append('limit', params.limit.toString());
    if (params.skip) queryParams.append('skip', params.skip.toString());

    const url = `${this.nppesBaseUrl}/?${queryParams.toString()}`;

    try {
      const response = await fetch(url);
      
      if (!response.ok) {
        throw new Error(`NPPES API error: ${response.status} ${response.statusText}`);
      }

      const data = await response.json() as NPPESResponse;
      return data.results || [];
    } catch (error) {
      console.error('[ProviderDirectoryApi] NPPES search failed:', error);
      throw error;
    }
  }

  // ==========================================================================
  // FHIR Resource Mapping
  // ==========================================================================

  /**
   * Map NPPES result to FHIR Practitioner resource
   * For NPI-1 (individual providers)
   */
  mapNPPESToPractitioner(nppes: NPPESResult): Practitioner {
    if (nppes.enumeration_type !== 'NPI-1') {
      throw new Error(`Cannot map NPI-2 (organization) to Practitioner. Use mapNPPESToOrganization instead.`);
    }

    const practitioner: Practitioner = {
      resourceType: 'Practitioner',
      id: nppes.number,
      meta: {
        profile: ['http://hl7.org/fhir/us/core/StructureDefinition/us-core-practitioner'],
        lastUpdated: nppes.basic.last_updated || new Date().toISOString()
      },
      identifier: [
        {
          system: 'http://hl7.org/fhir/sid/us-npi',
          value: nppes.number
        }
      ],
      active: !nppes.basic.deactivation_date || !!nppes.basic.reactivation_date,
      name: [this.mapNPPESToHumanName(nppes.basic)],
      gender: this.mapGender(nppes.basic.gender),
      address: this.mapNPPESAddresses(nppes.addresses),
      telecom: this.mapNPPESTelecom(nppes.addresses),
      qualification: this.mapNPPESQualifications(nppes)
    };

    return practitioner;
  }

  /**
   * Map NPPES result to FHIR Organization resource
   * For NPI-2 (organizational providers)
   */
  mapNPPESToOrganization(nppes: NPPESResult): Organization {
    if (nppes.enumeration_type !== 'NPI-2') {
      throw new Error(`Cannot map NPI-1 (individual) to Organization. Use mapNPPESToPractitioner instead.`);
    }

    const organization: Organization = {
      resourceType: 'Organization',
      id: nppes.number,
      meta: {
        profile: ['http://hl7.org/fhir/us/core/StructureDefinition/us-core-organization'],
        lastUpdated: nppes.basic.last_updated || new Date().toISOString()
      },
      identifier: [
        {
          system: 'http://hl7.org/fhir/sid/us-npi',
          value: nppes.number
        }
      ],
      active: !nppes.basic.deactivation_date || !!nppes.basic.reactivation_date,
      type: this.mapOrganizationType(nppes.taxonomies),
      name: nppes.basic.organization_name || '',
      alias: nppes.other_names?.map(n => n.organization_name).filter(Boolean) as string[],
      address: this.mapNPPESAddresses(nppes.addresses),
      telecom: this.mapNPPESTelecom(nppes.addresses)
    };

    return organization;
  }

  /**
   * Map NPPES to FHIR PractitionerRole resource
   * Links Practitioner to Organization with specialty
   */
  mapNPPESToPractitionerRole(
    practitionerNppes: NPPESResult,
    organizationRef?: Reference
  ): PractitionerRole {
    const practitionerRole: PractitionerRole = {
      resourceType: 'PractitionerRole',
      id: `${practitionerNppes.number}-role`,
      meta: {
        profile: ['http://hl7.org/fhir/us/core/StructureDefinition/us-core-practitionerrole'],
        lastUpdated: practitionerNppes.basic.last_updated || new Date().toISOString()
      },
      active: !practitionerNppes.basic.deactivation_date || !!practitionerNppes.basic.reactivation_date,
      practitioner: {
        reference: `Practitioner/${practitionerNppes.number}`,
        display: this.formatProviderName(practitionerNppes.basic)
      },
      organization: organizationRef,
      code: this.mapTaxonomiesToRoleCode(practitionerNppes.taxonomies),
      specialty: this.mapTaxonomiesToSpecialty(practitionerNppes.taxonomies),
      location: this.mapNPPESToLocationReferences(practitionerNppes),
      telecom: this.mapNPPESTelecom(practitionerNppes.addresses)
    };

    return practitionerRole;
  }

  /**
   * Map NPPES address to FHIR Location resource
   */
  mapNPPESToLocation(nppes: NPPESResult, addressIndex: number = 0): Location {
    const address = nppes.addresses.find(a => a.address_purpose === 'LOCATION') || nppes.addresses[addressIndex];
    
    if (!address) {
      throw new Error(`No address found for NPI ${nppes.number} at index ${addressIndex}. Available addresses: ${nppes.addresses.length}`);
    }

    const location: Location = {
      resourceType: 'Location',
      id: `${nppes.number}-loc-${addressIndex}`,
      meta: {
        profile: ['http://hl7.org/fhir/us/core/StructureDefinition/us-core-location'],
        lastUpdated: nppes.basic.last_updated || new Date().toISOString()
      },
      status: (!nppes.basic.deactivation_date || !!nppes.basic.reactivation_date) ? 'active' : 'inactive',
      name: nppes.enumeration_type === 'NPI-2' 
        ? nppes.basic.organization_name 
        : this.formatProviderName(nppes.basic),
      mode: 'instance',
      type: this.mapLocationTypeFromTaxonomy(nppes.taxonomies),
      telecom: this.mapAddressToTelecom(address),
      address: this.mapNPPESAddressToFhir(address),
      managingOrganization: nppes.enumeration_type === 'NPI-2'
        ? { reference: `Organization/${nppes.number}` }
        : undefined
    };

    return location;
  }

  // ==========================================================================
  // Search Operations
  // ==========================================================================

  /**
   * Search Practitioners with FHIR-compliant parameters
   * @returns FHIR Bundle of Practitioner resources
   */
  async searchPractitioners(params: PractitionerSearchParams): Promise<Bundle> {
    // If NPI provided, do direct lookup
    if (params.npi) {
      const nppes = await this.lookupNPPES(params.npi);
      if (!nppes || nppes.enumeration_type !== 'NPI-1') {
        return this.createSearchBundle('Practitioner', []);
      }
      const practitioner = this.mapNPPESToPractitioner(nppes);
      return this.createSearchBundle('Practitioner', [practitioner]);
    }

    // Otherwise, search NPPES
    const results = await this.searchNPPES({
      first_name: params.given,
      last_name: params.family,
      city: params.city,
      state: params.state,
      postal_code: params.postalCode,
      taxonomy_description: params.specialty,
      enumeration_type: 'NPI-1',
      limit: params._count || 50,
      skip: params._page ? (params._page - 1) * (params._count || 50) : 0
    });

    const practitioners = results
      .filter(r => r.enumeration_type === 'NPI-1')
      .map(r => this.mapNPPESToPractitioner(r));

    return this.createSearchBundle('Practitioner', practitioners);
  }

  /**
   * Search Organizations with FHIR-compliant parameters
   * @returns FHIR Bundle of Organization resources
   */
  async searchOrganizations(params: OrganizationSearchParams): Promise<Bundle> {
    // If NPI provided, do direct lookup
    if (params.npi) {
      const nppes = await this.lookupNPPES(params.npi);
      if (!nppes || nppes.enumeration_type !== 'NPI-2') {
        return this.createSearchBundle('Organization', []);
      }
      const organization = this.mapNPPESToOrganization(nppes);
      return this.createSearchBundle('Organization', [organization]);
    }

    // Otherwise, search NPPES
    const results = await this.searchNPPES({
      organization_name: params.name,
      city: params.city,
      state: params.state,
      postal_code: params.postalCode,
      enumeration_type: 'NPI-2',
      limit: params._count || 50,
      skip: params._page ? (params._page - 1) * (params._count || 50) : 0
    });

    const organizations = results
      .filter(r => r.enumeration_type === 'NPI-2')
      .map(r => this.mapNPPESToOrganization(r));

    return this.createSearchBundle('Organization', organizations);
  }

  /**
   * Search PractitionerRoles with FHIR-compliant parameters
   * @returns FHIR Bundle of PractitionerRole resources
   */
  async searchPractitionerRoles(params: {
    practitioner?: string;
    organization?: string;
    specialty?: string;
    _page?: number;
    _count?: number;
  }): Promise<Bundle> {
    const roles: PractitionerRole[] = [];

    // If practitioner NPI provided
    if (params.practitioner) {
      const npi = params.practitioner.replace('Practitioner/', '');
      const nppes = await this.lookupNPPES(npi);
      if (nppes && nppes.enumeration_type === 'NPI-1') {
        const orgRef = params.organization 
          ? { reference: params.organization }
          : undefined;
        roles.push(this.mapNPPESToPractitionerRole(nppes, orgRef));
      }
    } else if (params.specialty) {
      // Search by specialty
      const results = await this.searchNPPES({
        taxonomy_description: params.specialty,
        enumeration_type: 'NPI-1',
        limit: params._count || 50
      });
      results.forEach(r => {
        if (r.enumeration_type === 'NPI-1') {
          roles.push(this.mapNPPESToPractitionerRole(r));
        }
      });
    }

    return this.createSearchBundle('PractitionerRole', roles);
  }

  /**
   * Search Locations with FHIR-compliant parameters
   * @returns FHIR Bundle of Location resources
   */
  async searchLocations(params: LocationSearchParams): Promise<Bundle> {
    const locations: Location[] = [];

    // If organization NPI provided
    if (params.organization) {
      const npi = params.organization.replace('Organization/', '');
      const nppes = await this.lookupNPPES(npi);
      if (nppes) {
        // Create a location for each address
        nppes.addresses.forEach((_, index) => {
          locations.push(this.mapNPPESToLocation(nppes, index));
        });
      }
    } else {
      // Search NPPES by location criteria
      const results = await this.searchNPPES({
        city: params.city,
        state: params.state,
        postal_code: params.postalCode,
        limit: params._count || 50
      });
      results.forEach(r => {
        r.addresses.forEach((_, index) => {
          locations.push(this.mapNPPESToLocation(r, index));
        });
      });
    }

    return this.createSearchBundle('Location', locations);
  }

  // ==========================================================================
  // Read Operations
  // ==========================================================================

  /**
   * Read Practitioner by NPI
   */
  async readPractitioner(npi: string): Promise<Practitioner | null> {
    const nppes = await this.lookupNPPES(npi);
    if (!nppes || nppes.enumeration_type !== 'NPI-1') {
      return null;
    }
    return this.mapNPPESToPractitioner(nppes);
  }

  /**
   * Read Organization by NPI
   */
  async readOrganization(npi: string): Promise<Organization | null> {
    const nppes = await this.lookupNPPES(npi);
    if (!nppes || nppes.enumeration_type !== 'NPI-2') {
      return null;
    }
    return this.mapNPPESToOrganization(nppes);
  }

  /**
   * Read PractitionerRole by practitioner NPI
   */
  async readPractitionerRole(practitionerNpi: string): Promise<PractitionerRole | null> {
    const nppes = await this.lookupNPPES(practitionerNpi);
    if (!nppes || nppes.enumeration_type !== 'NPI-1') {
      return null;
    }
    return this.mapNPPESToPractitionerRole(nppes);
  }

  /**
   * Read Location by ID (format: {NPI}-loc-{index})
   */
  async readLocation(locationId: string): Promise<Location | null> {
    const match = locationId.match(/^(\d{10})-loc-(\d+)$/);
    if (!match) {
      return null;
    }
    const [, npi, indexStr] = match;
    const nppes = await this.lookupNPPES(npi);
    if (!nppes) {
      return null;
    }
    const index = parseInt(indexStr, 10);
    if (index >= nppes.addresses.length) {
      return null;
    }
    return this.mapNPPESToLocation(nppes, index);
  }

  // ==========================================================================
  // Helper Methods
  // ==========================================================================

  /**
   * Validate NPI format (10 digits, passes Luhn check)
   */
  validateNPI(npi: string): boolean {
    if (!/^\d{10}$/.test(npi)) {
      return false;
    }
    // NPI Luhn validation using CMS-assigned prefix
    const npiWithPrefix = ProviderDirectoryApi.NPI_LUHN_PREFIX + npi;
    return this.luhnCheck(npiWithPrefix);
  }

  /**
   * Luhn check algorithm
   */
  private luhnCheck(value: string): boolean {
    let sum = 0;
    let isEven = false;
    for (let i = value.length - 1; i >= 0; i--) {
      let digit = parseInt(value[i], 10);
      if (isEven) {
        digit *= 2;
        if (digit > 9) {
          digit -= 9;
        }
      }
      sum += digit;
      isEven = !isEven;
    }
    return sum % 10 === 0;
  }

  private mapNPPESToHumanName(basic: NPPESBasicInfo): HumanName {
    const name: HumanName = {
      use: 'official',
      family: basic.last_name || '',
      given: [basic.first_name, basic.middle_name].filter(Boolean) as string[]
    };
    if (basic.name_prefix) {
      name.prefix = [basic.name_prefix];
    }
    if (basic.name_suffix || basic.credential) {
      name.suffix = [basic.name_suffix, basic.credential].filter(Boolean) as string[];
    }
    return name;
  }

  private mapGender(gender?: string): 'male' | 'female' | 'other' | 'unknown' {
    switch (gender?.toUpperCase()) {
      case 'M': return 'male';
      case 'F': return 'female';
      default: return 'unknown';
    }
  }

  private mapNPPESAddresses(addresses: NPPESAddress[]): Address[] {
    return addresses.map(addr => this.mapNPPESAddressToFhir(addr));
  }

  private mapNPPESAddressToFhir(addr: NPPESAddress): Address {
    return {
      use: addr.address_purpose === 'LOCATION' ? 'work' : 'billing',
      type: 'physical',
      line: [addr.address_1, addr.address_2].filter(Boolean) as string[],
      city: addr.city,
      state: addr.state,
      postalCode: addr.postal_code,
      country: addr.country_code
    };
  }

  private mapNPPESTelecom(addresses: NPPESAddress[]): ContactPoint[] {
    const telecom: ContactPoint[] = [];
    addresses.forEach(addr => {
      if (addr.telephone_number) {
        telecom.push({
          system: 'phone',
          value: this.formatPhoneNumber(addr.telephone_number),
          use: addr.address_purpose === 'LOCATION' ? 'work' : 'work'
        });
      }
      if (addr.fax_number) {
        telecom.push({
          system: 'fax',
          value: this.formatPhoneNumber(addr.fax_number),
          use: 'work'
        });
      }
    });
    return telecom;
  }

  private mapAddressToTelecom(addr: NPPESAddress): ContactPoint[] {
    const telecom: ContactPoint[] = [];
    if (addr.telephone_number) {
      telecom.push({
        system: 'phone',
        value: this.formatPhoneNumber(addr.telephone_number),
        use: 'work'
      });
    }
    if (addr.fax_number) {
      telecom.push({
        system: 'fax',
        value: this.formatPhoneNumber(addr.fax_number),
        use: 'work'
      });
    }
    return telecom;
  }

  private formatPhoneNumber(phone: string): string {
    // Remove non-digits
    const digits = phone.replace(/\D/g, '');
    if (digits.length === 10) {
      return `${digits.slice(0, 3)}-${digits.slice(3, 6)}-${digits.slice(6)}`;
    }
    return phone;
  }

  private mapNPPESQualifications(nppes: NPPESResult): Practitioner['qualification'] {
    return nppes.taxonomies.map((tax, index) => ({
      identifier: [{
        system: 'http://nucc.org/provider-taxonomy',
        value: tax.code
      }],
      code: {
        coding: [{
          system: 'http://nucc.org/provider-taxonomy',
          code: tax.code,
          display: tax.desc
        }]
      },
      period: tax.license ? {
        start: nppes.basic.enumeration_date
      } : undefined,
      issuer: tax.state ? {
        display: `State of ${tax.state}`
      } : undefined
    }));
  }

  private mapOrganizationType(taxonomies: NPPESTaxonomy[]): CodeableConcept[] {
    if (!taxonomies || taxonomies.length === 0) {
      return [{
        coding: [{
          system: 'http://terminology.hl7.org/CodeSystem/organization-type',
          code: 'prov',
          display: 'Healthcare Provider'
        }]
      }];
    }

    return taxonomies.map(tax => ({
      coding: [{
        system: 'http://nucc.org/provider-taxonomy',
        code: tax.code,
        display: tax.desc
      }]
    }));
  }

  private mapTaxonomiesToRoleCode(taxonomies: NPPESTaxonomy[]): CodeableConcept[] {
    // Map primary taxonomy to practitioner role code
    const primary = taxonomies.find(t => t.primary) || taxonomies[0];
    if (!primary) {
      return [];
    }

    return [{
      coding: [{
        system: 'http://nucc.org/provider-taxonomy',
        code: primary.code,
        display: primary.desc
      }]
    }];
  }

  private mapTaxonomiesToSpecialty(taxonomies: NPPESTaxonomy[]): CodeableConcept[] {
    return taxonomies.map(tax => ({
      coding: [{
        system: 'http://nucc.org/provider-taxonomy',
        code: tax.code,
        display: tax.desc
      }]
    }));
  }

  private mapNPPESToLocationReferences(nppes: NPPESResult): Reference[] {
    return nppes.addresses
      .filter(a => a.address_purpose === 'LOCATION')
      .map((_, index) => ({
        reference: `Location/${nppes.number}-loc-${index}`
      }));
  }

  private mapLocationTypeFromTaxonomy(taxonomies: NPPESTaxonomy[]): CodeableConcept[] {
    // Default location type based on taxonomy
    const primary = taxonomies.find(t => t.primary) || taxonomies[0];
    
    return [{
      coding: [{
        system: 'http://terminology.hl7.org/CodeSystem/v3-RoleCode',
        code: primary?.desc?.toLowerCase().includes('hospital') ? 'HOSP' : 'OF',
        display: primary?.desc?.toLowerCase().includes('hospital') ? 'Hospital' : 'Outpatient facility'
      }]
    }];
  }

  private formatProviderName(basic: NPPESBasicInfo): string {
    if (basic.organization_name) {
      return basic.organization_name;
    }
    const parts = [basic.first_name, basic.middle_name, basic.last_name].filter(Boolean);
    if (basic.credential) {
      parts.push(basic.credential);
    }
    return parts.join(' ');
  }

  private createSearchBundle(resourceType: string, resources: any[]): Bundle {
    return {
      resourceType: 'Bundle',
      type: 'searchset',
      total: resources.length,
      link: [{
        relation: 'self',
        url: `${resourceType}?_count=${resources.length}`
      }],
      entry: resources.map(resource => ({
        fullUrl: `${resourceType}/${resource.id}`,
        resource,
        search: {
          mode: 'match'
        }
      }))
    };
  }
}

/**
 * Factory function to create Provider Directory API instance
 */
export function createProviderDirectoryApi(
  nppesBaseUrl?: string,
  cosmosEndpoint?: string,
  cosmosDatabase?: string,
  cosmosContainer?: string
): ProviderDirectoryApi {
  return new ProviderDirectoryApi(nppesBaseUrl, cosmosEndpoint, cosmosDatabase, cosmosContainer);
}
