#!/usr/bin/env node
/**
 * CloudHealthOffice Whitepaper PDF Generator
 * 
 * Converts WHITEPAPER-CMS-0057-F-COMPLIANCE.md to PDF using pandoc
 * with weasyprint for high-quality PDF output.
 * 
 * Prerequisites:
 *   - pandoc: https://pandoc.org/installing.html
 *   - weasyprint: pip install weasyprint
 *   - mermaid-filter (optional): npm install -g mermaid-filter
 * 
 * Usage:
 *   npx ts-node scripts/generate-whitepaper-pdf.ts
 *   npm run generate-pdf
 */

import { execSync } from 'child_process';
import { existsSync, mkdirSync } from 'fs';
import { join } from 'path';

const DOCS_DIR = join(__dirname, '..', 'docs');
const INPUT_FILE = join(DOCS_DIR, 'WHITEPAPER-CMS-0057-F-COMPLIANCE.md');
const OUTPUT_FILE = join(DOCS_DIR, 'WHITEPAPER-CMS-0057-F-COMPLIANCE.pdf');
const CSS_FILE = join(DOCS_DIR, 'whitepaper-style.css');
const GENERATED_DIR = join(__dirname, '..', 'generated');

interface CommandResult {
    success: boolean;
    output?: string;
    error?: string;
}

/**
 * Execute a command and return the result
 */
function runCommand(command: string): CommandResult {
    try {
        const output = execSync(command, { encoding: 'utf-8', stdio: 'pipe' });
        return { success: true, output };
    } catch (error: unknown) {
        const err = error as { message?: string; stderr?: string };
        return { 
            success: false, 
            error: err.stderr || err.message || 'Unknown error'
        };
    }
}

/**
 * Check if a command is available
 */
function commandExists(command: string): boolean {
    const checkCmd = process.platform === 'win32' 
        ? `where ${command}` 
        : `which ${command}`;
    return runCommand(checkCmd).success;
}

/**
 * Install prerequisites if missing
 */
function checkPrerequisites(): boolean {
    console.log('🔍 Checking prerequisites...\n');

    // Check pandoc
    if (!commandExists('pandoc')) {
        console.error('❌ pandoc is not installed.');
        console.log('   Install from: https://pandoc.org/installing.html');
        console.log('   - macOS: brew install pandoc');
        console.log('   - Ubuntu: apt-get install pandoc');
        console.log('   - Windows: choco install pandoc');
        return false;
    }
    console.log('✅ pandoc found');

    // Check weasyprint
    if (!commandExists('weasyprint')) {
        console.error('❌ weasyprint is not installed.');
        console.log('   Install with: pip install weasyprint');
        return false;
    }
    console.log('✅ weasyprint found');

    // Check mermaid-filter (optional)
    if (commandExists('mermaid-filter')) {
        console.log('✅ mermaid-filter found (Mermaid diagrams will be rendered)');
    } else {
        console.log('⚠️  mermaid-filter not found (Mermaid code blocks will be shown as-is)');
        console.log('   Optional install: npm install -g mermaid-filter');
    }

    return true;
}

/**
 * Ensure generated directory exists
 */
function ensureDirectories(): void {
    if (!existsSync(GENERATED_DIR)) {
        mkdirSync(GENERATED_DIR, { recursive: true });
        console.log(`📁 Created directory: ${GENERATED_DIR}`);
    }
}

/**
 * Generate the PDF using pandoc
 */
function generatePDF(): boolean {
    console.log('\n📄 Generating PDF...\n');

    // Check if input file exists
    if (!existsSync(INPUT_FILE)) {
        console.error(`❌ Input file not found: ${INPUT_FILE}`);
        return false;
    }

    // Check if CSS file exists
    if (!existsSync(CSS_FILE)) {
        console.error(`❌ CSS file not found: ${CSS_FILE}`);
        console.log('   Ensure docs/whitepaper-style.css exists before running.');
        return false;
    }

    // Build pandoc command
    const hasMermaidFilter = commandExists('mermaid-filter');
    
    let pandocCmd = [
        'pandoc',
        `"${INPUT_FILE}"`,
        '-o', `"${OUTPUT_FILE}"`,
        '--from', 'markdown+footnotes+pipe_tables+strikeout',
        '--to', 'pdf',
        '--pdf-engine=weasyprint',
        `--css="${CSS_FILE}"`,
        '--metadata', 'title="CloudHealthOffice CMS-0057-F Compliance Whitepaper"',
        '--metadata', 'author="Aurelianware"',
        '--metadata', 'date="November 2025"',
        '--toc',
        '--toc-depth=3',
        '--standalone',
        '--highlight-style=tango',
    ];

    // Add mermaid filter if available
    if (hasMermaidFilter) {
        pandocCmd.push('--filter', 'mermaid-filter');
    }

    const command = pandocCmd.join(' ');
    console.log('Running pandoc conversion...\n');

    const result = runCommand(command);

    if (result.success) {
        console.log(`✅ PDF generated successfully: ${OUTPUT_FILE}`);
        return true;
    } else {
        console.error('❌ PDF generation failed:');
        console.error(result.error);
        
        // Try alternative without weasyprint
        console.log('\n🔄 Trying alternative PDF engine (pdflatex)...');
        const altPandocCmd = [
            'pandoc',
            `"${INPUT_FILE}"`,
            '-o', `"${OUTPUT_FILE}"`,
            '--from', 'markdown+footnotes+pipe_tables+strikeout',
            '--to', 'pdf',
            '--pdf-engine=pdflatex',
            '--metadata', 'title="CloudHealthOffice CMS-0057-F Compliance Whitepaper"',
            '--metadata', 'author="Aurelianware"',
            '--metadata', 'date="November 2025"',
            '--toc',
            '--toc-depth=3',
            '--standalone',
            '--highlight-style=tango',
        ];
        
        if (hasMermaidFilter) {
            altPandocCmd.push('--filter', 'mermaid-filter');
        }
        
        const altCmd = altPandocCmd.join(' ');
        
        const altResult = runCommand(altCmd);
        if (altResult.success) {
            console.log(`✅ PDF generated with pdflatex: ${OUTPUT_FILE}`);
            return true;
        } else {
            console.error('❌ Alternative PDF generation also failed.');
            console.error('   Please ensure pandoc and weasyprint are properly installed.');
            return false;
        }
    }
}

/**
 * Print usage information
 */
function printUsage(): void {
    console.log(`
CloudHealthOffice Whitepaper PDF Generator
==========================================

This script converts the CMS-0057-F compliance whitepaper from Markdown to PDF.

Prerequisites:
  1. pandoc - Universal document converter
     Install: https://pandoc.org/installing.html
     
  2. weasyprint - PDF rendering engine
     Install: pip install weasyprint
     
  3. mermaid-filter (optional) - Render Mermaid diagrams
     Install: npm install -g mermaid-filter

Usage:
  npx ts-node scripts/generate-whitepaper-pdf.ts
  npm run generate-pdf

Output:
  docs/WHITEPAPER-CMS-0057-F-COMPLIANCE.pdf

Note on Mermaid Diagrams:
  If mermaid-filter is not installed, Mermaid code blocks will appear
  as syntax-highlighted code in the PDF. For rendered diagrams, install
  mermaid-filter and ensure Chrome/Chromium is available.
`);
}

/**
 * Main entry point
 */
function main(): void {
    console.log('═══════════════════════════════════════════════════════════════');
    console.log('  CloudHealthOffice Whitepaper PDF Generator');
    console.log('═══════════════════════════════════════════════════════════════\n');

    // Check for help flag
    if (process.argv.includes('--help') || process.argv.includes('-h')) {
        printUsage();
        process.exit(0);
    }

    // Check prerequisites
    if (!checkPrerequisites()) {
        console.log('\n❌ Prerequisites not met. Please install missing dependencies.');
        process.exit(1);
    }

    // Ensure directories exist
    ensureDirectories();

    // Generate PDF
    const success = generatePDF();

    if (success) {
        console.log('\n═══════════════════════════════════════════════════════════════');
        console.log('  ✅ Whitepaper PDF generation complete!');
        console.log(`  📄 Output: ${OUTPUT_FILE}`);
        console.log('═══════════════════════════════════════════════════════════════\n');
        process.exit(0);
    } else {
        console.log('\n═══════════════════════════════════════════════════════════════');
        console.log('  ❌ PDF generation failed. See errors above.');
        console.log('═══════════════════════════════════════════════════════════════\n');
        process.exit(1);
    }
}

// Run main
main();
