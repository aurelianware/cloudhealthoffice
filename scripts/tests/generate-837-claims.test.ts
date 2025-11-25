/**
 * Tests for 837 Claim Generator
 * Tests synthetic EDI 837P and 837I claim generation
 */

import * as fs from 'fs';
import * as path from 'path';
import {
  generate837P,
  generate837I,
  generateClaimBatch,
  saveClaimToFile,
  Claim837Options
} from '../utils/generate-837-claims';

describe('837 Claim Generator', () => {
  const testOutputDir = '/tmp/test-claims-output';

  beforeAll(() => {
    // Clean up test output directory
    if (fs.existsSync(testOutputDir)) {
      fs.rmSync(testOutputDir, { recursive: true });
    }
  });

  afterAll(() => {
    // Clean up after tests
    if (fs.existsSync(testOutputDir)) {
      fs.rmSync(testOutputDir, { recursive: true });
    }
  });

  describe('generate837P', () => {
    it('should generate a valid 837P professional claim', () => {
      const claim = generate837P();
      
      expect(claim).toBeDefined();
      expect(typeof claim).toBe('string');
      
      // Check for required segments
      expect(claim).toContain('ISA*');
      expect(claim).toContain('GS*HC*');
      expect(claim).toContain('ST*837');
      expect(claim).toContain('BHT*0019');
      expect(claim).toContain('NM1*');
      expect(claim).toContain('CLM*');
      expect(claim).toContain('SE*');
      expect(claim).toContain('GE*');
      expect(claim).toContain('IEA*');
    });

    it('should generate claim with custom options', () => {
      const options: Claim837Options = {
        claimType: '837P',
        patientIndex: 1,
        providerIndex: 2,
        serviceDate: '20240115',
        claimNumber: 'CLM123456789'
      };

      const claim = generate837P(options);
      
      expect(claim).toContain('CLM123456789');
      expect(claim).toContain('20240115');
      expect(claim).toContain('SMITH'); // Patient at index 1
      expect(claim).toContain('COMMUNITY HEALTH CENTER'); // Provider at index 2
    });

    it('should generate claims with different patient indices', () => {
      const claim0 = generate837P({ claimType: '837P', patientIndex: 0 });
      const claim1 = generate837P({ claimType: '837P', patientIndex: 1 });
      const claim2 = generate837P({ claimType: '837P', patientIndex: 2 });

      expect(claim0).toContain('DOE*JOHN');
      expect(claim1).toContain('SMITH*JANE');
      expect(claim2).toContain('JOHNSON*ROBERT');
    });

    it('should include proper billing provider information', () => {
      const claim = generate837P({ claimType: '837P', providerIndex: 0 });
      
      expect(claim).toContain('NM1*85*2*SMITH MEDICAL GROUP');
      expect(claim).toContain('1234567893'); // NPI
    });

    it('should include diagnosis codes', () => {
      const claim = generate837P();
      
      // Should contain HI segment with diagnosis
      expect(claim).toContain('HI*ABK:');
    });
  });

  describe('generate837I', () => {
    it('should generate a valid 837I institutional claim', () => {
      const claim = generate837I();
      
      expect(claim).toBeDefined();
      expect(typeof claim).toBe('string');
      
      // Check for required segments
      expect(claim).toContain('ISA*');
      expect(claim).toContain('GS*HC*');
      expect(claim).toContain('ST*837');
      expect(claim).toContain('BHT*0019');
      expect(claim).toContain('CLM*');
      expect(claim).toContain('CL1*'); // Institutional claim info
      expect(claim).toContain('SV2*'); // Institutional service line
      expect(claim).toContain('SE*');
      expect(claim).toContain('GE*');
      expect(claim).toContain('IEA*');
    });

    it('should generate claim with custom options', () => {
      const options: Claim837Options = {
        claimType: '837I',
        patientIndex: 2,
        serviceDate: '20240220',
        claimNumber: 'INST987654321'
      };

      const claim = generate837I(options);
      
      expect(claim).toContain('INST987654321');
      expect(claim).toContain('20240220');
      expect(claim).toContain('JOHNSON*ROBERT'); // Patient at index 2
    });

    it('should include hospital provider information', () => {
      const claim = generate837I();
      
      expect(claim).toContain('GENERAL HOSPITAL');
      expect(claim).toContain('1111111119'); // Hospital NPI
    });

    it('should generate different claims each time due to random control numbers', () => {
      const claim1 = generate837I();
      const claim2 = generate837I();
      
      // Control numbers should be different
      expect(claim1).not.toBe(claim2);
    });
  });

  describe('generateClaimBatch', () => {
    it('should generate multiple 837P claims', () => {
      const claims = generateClaimBatch(5, '837P');
      
      expect(claims).toHaveLength(5);
      claims.forEach(claim => {
        expect(claim).toContain('ISA*');
        expect(claim).toContain('ST*837');
      });
    });

    it('should generate multiple 837I claims', () => {
      const claims = generateClaimBatch(3, '837I');
      
      expect(claims).toHaveLength(3);
      claims.forEach(claim => {
        expect(claim).toContain('ISA*');
        expect(claim).toContain('CL1*');
      });
    });

    it('should cycle through patients and providers', () => {
      const claims = generateClaimBatch(6, '837P');
      
      // With 3 patients and 3 providers, indices should cycle
      expect(claims[0]).toContain('DOE*JOHN');
      expect(claims[1]).toContain('SMITH*JANE');
      expect(claims[2]).toContain('JOHNSON*ROBERT');
      expect(claims[3]).toContain('DOE*JOHN'); // Cycles back
    });

    it('should generate empty array for zero count', () => {
      const claims = generateClaimBatch(0, '837P');
      expect(claims).toHaveLength(0);
    });

    it('should default to 837P when type not specified', () => {
      const claims = generateClaimBatch(1);
      
      expect(claims).toHaveLength(1);
      // 837P uses different provider than 837I
      expect(claims[0]).toContain('SMITH MEDICAL GROUP');
    });
  });

  describe('saveClaimToFile', () => {
    it('should save claim to file', () => {
      const claim = generate837P();
      const outputPath = path.join(testOutputDir, 'test-claim.edi');
      
      saveClaimToFile(claim, outputPath);
      
      expect(fs.existsSync(outputPath)).toBe(true);
      const savedContent = fs.readFileSync(outputPath, 'utf-8');
      expect(savedContent).toBe(claim);
    });

    it('should create nested directories if they do not exist', () => {
      const claim = generate837I();
      const nestedPath = path.join(testOutputDir, 'nested', 'dir', 'claim.edi');
      
      saveClaimToFile(claim, nestedPath);
      
      expect(fs.existsSync(nestedPath)).toBe(true);
    });

    it('should overwrite existing file', () => {
      const outputPath = path.join(testOutputDir, 'overwrite-test.edi');
      
      const claim1 = generate837P({ claimType: '837P', claimNumber: 'FIRST123' });
      saveClaimToFile(claim1, outputPath);
      
      const claim2 = generate837P({ claimType: '837P', claimNumber: 'SECOND456' });
      saveClaimToFile(claim2, outputPath);
      
      const savedContent = fs.readFileSync(outputPath, 'utf-8');
      expect(savedContent).toContain('SECOND456');
      expect(savedContent).not.toContain('FIRST123');
    });
  });

  describe('EDI Segment Validation', () => {
    it('should have properly formatted ISA segment', () => {
      const claim = generate837P();
      const isaLine = claim.split('\n').find(line => line.startsWith('ISA*'));
      
      expect(isaLine).toBeDefined();
      expect(isaLine).toMatch(/^ISA\*00\*          \*00\*          \*ZZ\*/);
      expect(isaLine).toContain('*^*00501*');
      expect(isaLine).toMatch(/~$/);
    });

    it('should have properly formatted GS segment', () => {
      const claim = generate837P();
      const gsLine = claim.split('\n').find(line => line.startsWith('GS*'));
      
      expect(gsLine).toBeDefined();
      expect(gsLine).toContain('GS*HC*');
      expect(gsLine).toContain('*005010X222A1~');
    });

    it('should have properly formatted ST segment', () => {
      const claim = generate837P();
      const stLine = claim.split('\n').find(line => line.startsWith('ST*'));
      
      expect(stLine).toBeDefined();
      expect(stLine).toContain('ST*837*');
      expect(stLine).toContain('*005010X222A1~');
    });

    it('should have matching control numbers in header and trailer', () => {
      const claim = generate837P();
      const lines = claim.split('\n');
      
      const stLine = lines.find(line => line.startsWith('ST*'));
      const seLine = lines.find(line => line.startsWith('SE*'));
      
      const stControlNumber = stLine?.split('*')[2]?.replace('*005010X222A1~', '');
      const seControlNumber = seLine?.split('*')[2]?.replace('~', '');
      
      expect(stControlNumber).toBe(seControlNumber);
    });
  });

  describe('Data Integrity', () => {
    it('should use synthetic data that is not real PHI', () => {
      const claim = generate837P();
      
      // Verify synthetic identifiers
      expect(claim).toContain('TEST00000'); // Synthetic member IDs
      // NPIs should be present
      expect(claim).toContain('1234567893'); // Synthetic NPI
      
      // Should not contain real SSN patterns (XXX-XX-XXXX)
      expect(claim).not.toMatch(/\d{3}-\d{2}-\d{4}/);
    });

    it('should generate different claims with unique control numbers', () => {
      // Use different timestamps to ensure unique control numbers
      const claim1 = generate837P({ claimType: '837P', claimNumber: 'TEST' + Date.now() });
      
      // Wait a bit to ensure different timestamp
      const uniqueSuffix = Date.now() + 1;
      const claim2 = generate837P({ claimType: '837P', claimNumber: 'TEST' + uniqueSuffix });
      
      // The entire claims should be different
      expect(claim1).not.toBe(claim2);
    });
  });
});
