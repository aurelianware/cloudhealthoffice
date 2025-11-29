/**
 * Cloud Health Office - QNXT Eligibility Rules Migration
 * 
 * Script to import existing QNXT eligibility rules from CSV format.
 * Supports bulk import and validation of eligibility rules.
 * 
 * CSV Format Expected:
 * rule_id,rule_name,description,plan_code,service_type_code,benefit_category,
 * coverage_indicator,prior_auth_required,referral_required,
 * in_network_copay,in_network_coinsurance,in_network_deductible_applies,
 * out_network_copay,out_network_coinsurance,out_network_deductible_applies,out_network_coverage_percent,
 * max_quantity,quantity_period,max_amount,amount_period,
 * min_age,max_age,gender_restrictions,
 * effective_start_date,effective_end_date,priority,is_active
 */

import * as fs from 'fs';
import * as path from 'path';
import { QNXTEligibilityRule } from './types';

/**
 * Parse CSV line handling quoted values
 */
function parseCSVLine(line: string): string[] {
  const result: string[] = [];
  let current = '';
  let inQuotes = false;
  
  for (let i = 0; i < line.length; i++) {
    const char = line[i];
    
    if (char === '"') {
      if (inQuotes && line[i + 1] === '"') {
        current += '"';
        i++; // Skip next quote
      } else {
        inQuotes = !inQuotes;
      }
    } else if (char === ',' && !inQuotes) {
      result.push(current.trim());
      current = '';
    } else {
      current += char;
    }
  }
  
  result.push(current.trim());
  return result;
}

/**
 * Parse boolean from CSV value
 */
function parseBoolean(value: string): boolean {
  const lower = value.toLowerCase().trim();
  return lower === 'true' || lower === 'yes' || lower === '1' || lower === 'y';
}

/**
 * Parse number from CSV value (returns undefined if empty or invalid)
 */
function parseNumber(value: string): number | undefined {
  if (!value || value.trim() === '') return undefined;
  const num = parseFloat(value);
  return isNaN(num) ? undefined : num;
}

/**
 * Parse coverage indicator from CSV value
 */
function parseCoverageIndicator(value: string): 'covered' | 'not_covered' | 'limited' | 'excluded' {
  const lower = value.toLowerCase().trim();
  switch (lower) {
    case 'covered':
    case 'c':
    case '1':
      return 'covered';
    case 'not_covered':
    case 'not covered':
    case 'nc':
    case '0':
      return 'not_covered';
    case 'limited':
    case 'l':
      return 'limited';
    case 'excluded':
    case 'e':
    case 'x':
      return 'excluded';
    default:
      return 'covered';
  }
}

/**
 * Parse time period from CSV value
 */
function parsePeriod(value: string): 'day' | 'week' | 'month' | 'year' | 'lifetime' | undefined {
  if (!value || value.trim() === '') return undefined;
  const lower = value.toLowerCase().trim();
  switch (lower) {
    case 'day':
    case 'd':
      return 'day';
    case 'week':
    case 'w':
      return 'week';
    case 'month':
    case 'm':
      return 'month';
    case 'year':
    case 'y':
    case 'annual':
      return 'year';
    case 'lifetime':
    case 'life':
    case 'l':
      return 'lifetime';
    default:
      return undefined;
  }
}

/**
 * Parse gender restrictions from CSV value
 */
function parseGenderRestrictions(value: string): ('M' | 'F')[] | undefined {
  if (!value || value.trim() === '') return undefined;
  const genders: ('M' | 'F')[] = [];
  const upper = value.toUpperCase().trim();
  if (upper.includes('M') || upper.includes('MALE')) {
    genders.push('M');
  }
  if (upper.includes('F') || upper.includes('FEMALE')) {
    genders.push('F');
  }
  return genders.length > 0 ? genders : undefined;
}

/**
 * Convert CSV row to QNXTEligibilityRule
 */
function csvRowToRule(headers: string[], values: string[]): QNXTEligibilityRule {
  const getValue = (key: string): string => {
    const index = headers.findIndex(h => h.toLowerCase().replace(/[_\s]/g, '') === key.toLowerCase().replace(/[_\s]/g, ''));
    return index >= 0 && index < values.length ? values[index] : '';
  };
  
  const rule: QNXTEligibilityRule = {
    ruleId: getValue('ruleid') || getValue('rule_id') || `rule_${Date.now()}`,
    ruleName: getValue('rulename') || getValue('rule_name') || 'Unnamed Rule',
    description: getValue('description') || undefined,
    planCode: getValue('plancode') || getValue('plan_code') || 'DEFAULT',
    serviceTypeCode: getValue('servicetypecode') || getValue('service_type_code') || '30',
    benefitCategory: getValue('benefitcategory') || getValue('benefit_category') || 'General',
    coverageIndicator: parseCoverageIndicator(getValue('coverageindicator') || getValue('coverage_indicator')),
    priorAuthRequired: parseBoolean(getValue('priorauthrequired') || getValue('prior_auth_required') || 'false'),
    referralRequired: parseBoolean(getValue('referralrequired') || getValue('referral_required') || 'false'),
    effectiveDateRange: {
      startDate: getValue('effectivestartdate') || getValue('effective_start_date') || '20000101',
      endDate: getValue('effectiveenddate') || getValue('effective_end_date') || undefined
    },
    priority: parseNumber(getValue('priority')) || 100,
    isActive: parseBoolean(getValue('isactive') || getValue('is_active') || 'true')
  };
  
  // In-network requirements
  const inNetworkCopay = parseNumber(getValue('innetworkcopay') || getValue('in_network_copay'));
  const inNetworkCoinsurance = parseNumber(getValue('innetworkcoinsurance') || getValue('in_network_coinsurance'));
  const inNetworkDeductible = parseBoolean(getValue('innetworkdeductibleapplies') || getValue('in_network_deductible_applies') || 'true');
  
  if (inNetworkCopay !== undefined || inNetworkCoinsurance !== undefined) {
    rule.inNetworkRequirements = {
      copay: inNetworkCopay,
      coinsurance: inNetworkCoinsurance,
      deductibleApplies: inNetworkDeductible
    };
  }
  
  // Out-of-network requirements
  const outNetworkCopay = parseNumber(getValue('outnetworkcopay') || getValue('out_network_copay'));
  const outNetworkCoinsurance = parseNumber(getValue('outnetworkcoinsurance') || getValue('out_network_coinsurance'));
  const outNetworkDeductible = parseBoolean(getValue('outnetworkdeductibleapplies') || getValue('out_network_deductible_applies') || 'true');
  const outNetworkCoverage = parseNumber(getValue('outnetworkcoveragepercent') || getValue('out_network_coverage_percent'));
  
  if (outNetworkCopay !== undefined || outNetworkCoinsurance !== undefined || outNetworkCoverage !== undefined) {
    rule.outOfNetworkRequirements = {
      copay: outNetworkCopay,
      coinsurance: outNetworkCoinsurance,
      deductibleApplies: outNetworkDeductible,
      coveragePercent: outNetworkCoverage
    };
  }
  
  // Quantity limits
  const maxQuantity = parseNumber(getValue('maxquantity') || getValue('max_quantity'));
  const quantityPeriod = parsePeriod(getValue('quantityperiod') || getValue('quantity_period'));
  
  if (maxQuantity !== undefined) {
    rule.quantityLimits = {
      maxQuantity,
      quantityPeriod
    };
  }
  
  // Dollar limits
  const maxAmount = parseNumber(getValue('maxamount') || getValue('max_amount'));
  const amountPeriod = parsePeriod(getValue('amountperiod') || getValue('amount_period'));
  
  if (maxAmount !== undefined) {
    rule.dollarLimits = {
      maxAmount,
      amountPeriod
    };
  }
  
  // Age limits
  const minAge = parseNumber(getValue('minage') || getValue('min_age'));
  const maxAge = parseNumber(getValue('maxage') || getValue('max_age'));
  
  if (minAge !== undefined || maxAge !== undefined) {
    rule.ageLimits = {
      minAge,
      maxAge
    };
  }
  
  // Gender restrictions
  const genderRestrictions = parseGenderRestrictions(getValue('genderrestrictions') || getValue('gender_restrictions'));
  if (genderRestrictions) {
    rule.genderRestrictions = genderRestrictions;
  }
  
  return rule;
}

/**
 * Load QNXT eligibility rules from CSV file
 */
export async function loadQNXTRulesFromCSV(filePath: string): Promise<QNXTEligibilityRule[]> {
  const absolutePath = path.resolve(filePath);
  
  if (!fs.existsSync(absolutePath)) {
    throw new Error(`CSV file not found: ${absolutePath}`);
  }
  
  const content = fs.readFileSync(absolutePath, 'utf-8');
  const lines = content.split('\n').filter(line => line.trim() !== '');
  
  if (lines.length < 2) {
    throw new Error('CSV file must have header row and at least one data row');
  }
  
  const headers = parseCSVLine(lines[0]);
  const rules: QNXTEligibilityRule[] = [];
  const errors: string[] = [];
  
  for (let i = 1; i < lines.length; i++) {
    try {
      const values = parseCSVLine(lines[i]);
      const rule = csvRowToRule(headers, values);
      rules.push(rule);
    } catch (error) {
      errors.push(`Row ${i + 1}: ${error instanceof Error ? error.message : String(error)}`);
    }
  }
  
  if (errors.length > 0) {
    console.warn(`Encountered ${errors.length} errors during CSV import:`);
    errors.slice(0, 10).forEach(e => console.warn(`  - ${e}`));
    if (errors.length > 10) {
      console.warn(`  ... and ${errors.length - 10} more errors`);
    }
  }
  
  return rules;
}

/**
 * Validate eligibility rules
 */
export function validateRules(rules: QNXTEligibilityRule[]): { valid: boolean; errors: string[] } {
  const errors: string[] = [];
  
  for (const rule of rules) {
    // Check required fields
    if (!rule.ruleId) {
      errors.push(`Rule missing ruleId`);
    }
    if (!rule.planCode) {
      errors.push(`Rule ${rule.ruleId}: missing planCode`);
    }
    if (!rule.serviceTypeCode) {
      errors.push(`Rule ${rule.ruleId}: missing serviceTypeCode`);
    }
    if (!rule.effectiveDateRange?.startDate) {
      errors.push(`Rule ${rule.ruleId}: missing effectiveStartDate`);
    }
    
    // Validate date format
    const dateRegex = /^\d{8}$|^\d{4}-\d{2}-\d{2}$/;
    if (rule.effectiveDateRange?.startDate && !dateRegex.test(rule.effectiveDateRange.startDate)) {
      errors.push(`Rule ${rule.ruleId}: invalid effectiveStartDate format`);
    }
    if (rule.effectiveDateRange?.endDate && !dateRegex.test(rule.effectiveDateRange.endDate)) {
      errors.push(`Rule ${rule.ruleId}: invalid effectiveEndDate format`);
    }
    
    // Validate priority
    if (rule.priority < 0) {
      errors.push(`Rule ${rule.ruleId}: priority must be non-negative`);
    }
    
    // Validate percentages
    if (rule.inNetworkRequirements?.coinsurance !== undefined) {
      if (rule.inNetworkRequirements.coinsurance < 0 || rule.inNetworkRequirements.coinsurance > 100) {
        errors.push(`Rule ${rule.ruleId}: in-network coinsurance must be between 0 and 100`);
      }
    }
    if (rule.outOfNetworkRequirements?.coinsurance !== undefined) {
      if (rule.outOfNetworkRequirements.coinsurance < 0 || rule.outOfNetworkRequirements.coinsurance > 100) {
        errors.push(`Rule ${rule.ruleId}: out-of-network coinsurance must be between 0 and 100`);
      }
    }
  }
  
  return {
    valid: errors.length === 0,
    errors
  };
}

/**
 * Export rules to JSON format
 */
export function exportRulesToJSON(rules: QNXTEligibilityRule[], outputPath: string): void {
  const absolutePath = path.resolve(outputPath);
  fs.writeFileSync(absolutePath, JSON.stringify(rules, null, 2), 'utf-8');
}

/**
 * Generate sample CSV template
 */
export function generateSampleCSV(outputPath: string): void {
  const headers = [
    'rule_id',
    'rule_name',
    'description',
    'plan_code',
    'service_type_code',
    'benefit_category',
    'coverage_indicator',
    'prior_auth_required',
    'referral_required',
    'in_network_copay',
    'in_network_coinsurance',
    'in_network_deductible_applies',
    'out_network_copay',
    'out_network_coinsurance',
    'out_network_deductible_applies',
    'out_network_coverage_percent',
    'max_quantity',
    'quantity_period',
    'max_amount',
    'amount_period',
    'min_age',
    'max_age',
    'gender_restrictions',
    'effective_start_date',
    'effective_end_date',
    'priority',
    'is_active'
  ];
  
  const sampleRows = [
    [
      'RULE001',
      'Preventive Care - Annual Physical',
      'Coverage for annual preventive care visits',
      'PPO_GOLD',
      '30',
      'Preventive Care',
      'covered',
      'false',
      'false',
      '0',
      '0',
      'false',
      '50',
      '40',
      'true',
      '60',
      '1',
      'year',
      '',
      '',
      '',
      '',
      '',
      '20240101',
      '',
      '10',
      'true'
    ],
    [
      'RULE002',
      'Emergency Room Visit',
      'Emergency room services coverage',
      'PPO_GOLD',
      '85',
      'Emergency Services',
      'covered',
      'false',
      'false',
      '150',
      '20',
      'true',
      '250',
      '40',
      'true',
      '60',
      '',
      '',
      '',
      '',
      '',
      '',
      '',
      '20240101',
      '',
      '10',
      'true'
    ],
    [
      'RULE003',
      'Mental Health Outpatient',
      'Outpatient mental health services',
      'PPO_GOLD',
      'MH',
      'Mental Health',
      'covered',
      'false',
      'false',
      '30',
      '20',
      'true',
      '60',
      '40',
      'true',
      '50',
      '52',
      'year',
      '',
      '',
      '',
      '',
      '',
      '20240101',
      '',
      '20',
      'true'
    ],
    [
      'RULE004',
      'Physical Therapy',
      'Physical therapy sessions',
      'PPO_GOLD',
      'PT',
      'Physical Therapy',
      'covered',
      'true',
      'true',
      '40',
      '20',
      'true',
      '80',
      '40',
      'true',
      '50',
      '30',
      'year',
      '',
      '',
      '',
      '',
      '',
      '20240101',
      '',
      '30',
      'true'
    ]
  ];
  
  const content = [
    headers.join(','),
    ...sampleRows.map(row => row.join(','))
  ].join('\n');
  
  const absolutePath = path.resolve(outputPath);
  fs.writeFileSync(absolutePath, content, 'utf-8');
}

/**
 * CLI entry point for migration script
 */
async function main(): Promise<void> {
  const args = process.argv.slice(2);
  
  if (args.length === 0 || args.includes('--help') || args.includes('-h')) {
    console.log(`
QNXT Eligibility Rules Migration Tool

Usage:
  npx ts-node migration.ts import <csv-file> [--output <json-file>] [--validate]
  npx ts-node migration.ts sample <output-csv>
  npx ts-node migration.ts validate <csv-file>

Commands:
  import    Import rules from CSV file
  sample    Generate sample CSV template
  validate  Validate CSV file without importing

Options:
  --output, -o    Output JSON file path (for import command)
  --validate, -v  Validate rules after import
  --help, -h      Show this help message

Examples:
  npx ts-node migration.ts import qnxt-rules.csv --output rules.json --validate
  npx ts-node migration.ts sample sample-rules.csv
  npx ts-node migration.ts validate qnxt-rules.csv
    `);
    return;
  }
  
  const command = args[0];
  
  if (command === 'sample') {
    const outputFile = args[1] || 'sample-eligibility-rules.csv';
    generateSampleCSV(outputFile);
    console.log(`Sample CSV template generated: ${outputFile}`);
    return;
  }
  
  if (command === 'import' || command === 'validate') {
    const csvFile = args[1];
    if (!csvFile) {
      console.error('Error: CSV file path required');
      process.exit(1);
    }
    
    try {
      const rules = await loadQNXTRulesFromCSV(csvFile);
      console.log(`Loaded ${rules.length} rules from ${csvFile}`);
      
      // Validate
      const validation = validateRules(rules);
      if (!validation.valid) {
        console.error('\nValidation errors:');
        validation.errors.forEach(e => console.error(`  - ${e}`));
        if (command === 'validate') {
          process.exit(1);
        }
      } else {
        console.log('All rules validated successfully');
      }
      
      if (command === 'validate') {
        return;
      }
      
      // Export to JSON if output specified
      const outputIndex = args.findIndex(a => a === '--output' || a === '-o');
      if (outputIndex >= 0 && args[outputIndex + 1]) {
        const outputFile = args[outputIndex + 1];
        exportRulesToJSON(rules, outputFile);
        console.log(`Rules exported to ${outputFile}`);
      }
      
    } catch (error) {
      console.error('Error:', error instanceof Error ? error.message : String(error));
      process.exit(1);
    }
    return;
  }
  
  console.error(`Unknown command: ${command}`);
  process.exit(1);
}

// Run if executed directly
if (require.main === module) {
  main().catch(console.error);
}
