import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';

describe('generatePatientAccessSpecs', () => {
  const originalCwd = process.cwd();

  afterEach(() => {
    process.chdir(originalCwd);
  });

  it('writes capability statement and OpenAPI artifacts', () => {
    const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), 'patient-access-specs-'));
    process.chdir(tempDir);
    jest.resetModules();
    // Import after setting cwd so OUTPUT_DIR resolves into temp directory
    const { generatePatientAccessSpecs, branchCoverageSignals } = require('../setup/generate-patient-access-specs');

    generatePatientAccessSpecs();

    const outputDir = path.join(tempDir, 'generated', 'infra', 'patient-access-api');
    const capabilityPath = path.join(outputDir, 'capabilitystatement.json');
    const openApiPath = path.join(outputDir, 'openapi.yaml');

    expect(fs.existsSync(capabilityPath)).toBe(true);
    expect(fs.existsSync(openApiPath)).toBe(true);

    const capability = JSON.parse(fs.readFileSync(capabilityPath, 'utf-8'));
    expect(capability.resourceType).toBe('CapabilityStatement');
    expect(capability.rest[0].resource.map((resource: any) => resource.type)).toEqual(
      expect.arrayContaining(['Patient', 'Coverage', 'ExplanationOfBenefit', 'Claim'])
    );

    const openApi = fs.readFileSync(openApiPath, 'utf-8');
    expect(openApi).toContain('/Patient');
    expect(openApi).toContain('smartOnFhir');

    expect(branchCoverageSignals.createdOutputDirectory).toBe(true);
    expect(branchCoverageSignals.reusedExistingDirectory).toBe(false);
    expect(branchCoverageSignals.cliEntryPointExecuted).toBe(false);

    fs.rmSync(tempDir, { recursive: true, force: true });
  });

  it('overwrites artifacts on subsequent runs', () => {
    const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), 'patient-access-specs-'));
    process.chdir(tempDir);

    const capabilityPath = path.join(tempDir, 'generated', 'infra', 'patient-access-api', 'capabilitystatement.json');

    jest.resetModules();
    const { generatePatientAccessSpecs, branchCoverageSignals } = require('../setup/generate-patient-access-specs');

    generatePatientAccessSpecs();
    const firstTimestamp = JSON.parse(fs.readFileSync(capabilityPath, 'utf-8')).date;
    expect(branchCoverageSignals.createdOutputDirectory).toBe(true);
    expect(branchCoverageSignals.reusedExistingDirectory).toBe(false);
    expect(branchCoverageSignals.cliEntryPointExecuted).toBe(false);

    generatePatientAccessSpecs();
    const secondTimestamp = JSON.parse(fs.readFileSync(capabilityPath, 'utf-8')).date;
    expect(branchCoverageSignals.reusedExistingDirectory).toBe(true);
    expect(branchCoverageSignals.cliEntryPointExecuted).toBe(false);

    expect(new Date(secondTimestamp).getTime()).toBeGreaterThanOrEqual(new Date(firstTimestamp).getTime());

    fs.rmSync(tempDir, { recursive: true, force: true });
  });

  it('executes CLI entry point when forced via environment', () => {
    const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), 'patient-access-specs-cli-'));
    process.chdir(tempDir);
    process.env.GENERATE_PATIENT_ACCESS_SPECS_FORCE_CLI = 'true';

    jest.resetModules();
    const { branchCoverageSignals } = require('../setup/generate-patient-access-specs');

    const capabilityPath = path.join(tempDir, 'generated', 'infra', 'patient-access-api', 'capabilitystatement.json');

    expect(fs.existsSync(capabilityPath)).toBe(true);
    expect(branchCoverageSignals.cliEntryPointExecuted).toBe(true);
    expect(branchCoverageSignals.createdOutputDirectory).toBe(true);

    fs.rmSync(tempDir, { recursive: true, force: true });
    delete process.env.GENERATE_PATIENT_ACCESS_SPECS_FORCE_CLI;
  });
});
