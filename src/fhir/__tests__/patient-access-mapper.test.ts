describe('patient-access-mapper', () => {
    
  function loadModule() {
    jest.resetModules();
    const mockMapPatient = jest.fn().mockImplementation(patient => ({
      resourceType: 'Patient',
      id: patient.memberId,
      identifier: [{ value: patient.memberId }]
    }));
    const mockMapClaim = jest.fn().mockImplementation(claim => ({
      resourceType: 'Claim',
      id: claim.claimId
    }));
    const mockRedactFromProvider = jest.fn().mockImplementation(resource => ({
      ...resource,
      redacted: true
    }));
    const mockRedactPHI = jest.fn().mockImplementation(resource => ({
      ...resource,
      masked: true
    }));

    jest.doMock('../provider-access-api', () => ({
      ProviderAccessApi: jest.fn().mockImplementation(() => ({
        mapBackendPatientToFhir: mockMapPatient,
        mapBackendClaimToFhir: mockMapClaim,
        redactPhi: mockRedactFromProvider
      }))
    }));

    jest.doMock('../../security/hipaaLogger', () => ({
      redactPHI: mockRedactPHI
    }));

    let moduleExports: typeof import('../patient-access-mapper');
    jest.isolateModules(() => {
      moduleExports = require('../patient-access-mapper');
    });

    return {
      mockMapPatient,
      mockMapClaim,
      mockRedactFromProvider,
      mockRedactPHI,
      moduleExports: moduleExports!
    };
  }

  it('creates patient bundles using provider mappings', () => {
    const { moduleExports, mockMapPatient, mockRedactFromProvider } = loadModule();
    const bundle = moduleExports.patientsToBundle([
      {
        memberId: '123',
        firstName: 'Jane',
        lastName: 'Doe',
        dob: '2000-01-01',
        gender: 'female'
      } as any
    ], 'https://api.example/bundle');

    expect(mockMapPatient).toHaveBeenCalledTimes(1);
    expect(mockRedactFromProvider).toHaveBeenCalledWith(expect.objectContaining({ id: '123' }));
    expect(bundle.entry![0].fullUrl).toBe('Patient/123');
    expect(bundle.link![0].url).toBe('https://api.example/bundle');
  });

  it('creates coverage bundles and applies PHI redaction', () => {
    const { moduleExports, mockRedactPHI } = loadModule();
    const bundle = moduleExports.coverageToBundle([
      {
        memberId: '999',
        firstName: 'Alex',
        lastName: 'Smith',
        dob: '1995-03-15',
        gender: 'male'
      } as any
    ], 'self-link');

    expect(mockRedactPHI).toHaveBeenCalledWith(expect.objectContaining({ resourceType: 'Coverage' }));
    expect(bundle.entry![0].fullUrl).toBe('Coverage/999-COV');
    expect(bundle.total).toBe(1);
  });

  it('maps claims and payments into bundles with redaction', () => {
    const { moduleExports, mockMapClaim, mockRedactFromProvider } = loadModule();

    const claimsBundle = moduleExports.claimsToBundle([
      {
        claimId: 'CLM-1',
        memberId: '999',
        providerId: 'NPI123',
        claimType: 'professional',
        serviceDate: '2025-01-01',
        diagnosisCodes: [],
        procedureCodes: [],
        totalCharged: 100,
        totalPaid: 90,
        status: 'active'
      }
    ], 'claims-link');

    expect(mockMapClaim).toHaveBeenCalledWith(expect.objectContaining({ claimId: 'CLM-1' }));
    expect(mockRedactFromProvider).toHaveBeenCalledWith(expect.objectContaining({ resourceType: 'Claim' }));
    expect(claimsBundle.entry![0].fullUrl).toBe('Claim/CLM-1');

    const eobBundle = moduleExports.paymentsToEobBundle([
      {
        paymentId: 'PMT-1',
        claimId: 'CLM-1',
        memberId: '999',
        paymentDate: '2025-02-01',
        totalPaid: 120
      }
    ], 'eob-link');

    expect(mockRedactFromProvider).toHaveBeenCalledWith(expect.objectContaining({ resourceType: 'ExplanationOfBenefit' }));
    expect(eobBundle.entry![0].fullUrl).toBe('ExplanationOfBenefit/PMT-1');
  });
});
