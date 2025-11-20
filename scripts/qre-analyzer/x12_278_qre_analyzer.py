#!/usr/bin/env python3
"""
X12 278 X215 QRE Analyzer

Validates X12 278 Healthcare Services Review - Inquiry and Response (X215) transactions
against Availity QRE (Query and Response for Eligibility) requirements and best practices.

This analyzer ensures compliance with:
- X12 TR3 005010X215 specifications
- Availity QRE minimal data requirements
- Best practices for authorization inquiry transactions
"""

import json
import sys
import re
from typing import Dict, List, Tuple, Optional
from dataclasses import dataclass, field
from enum import Enum


class Severity(Enum):
    """Validation severity levels"""
    ERROR = "ERROR"
    WARNING = "WARNING"
    INFO = "INFO"


@dataclass
class ValidationResult:
    """Validation result for a single check"""
    severity: Severity
    code: str
    message: str
    segment: Optional[str] = None
    line_number: Optional[int] = None
    context: Dict = field(default_factory=dict)


@dataclass
class AnalysisReport:
    """Complete analysis report"""
    file_path: str
    tr3_version: str
    is_valid: bool
    error_count: int
    warning_count: int
    info_count: int
    results: List[ValidationResult]
    query_method: Optional[str] = None
    segments_found: List[str] = field(default_factory=list)


class X12_278_QRE_Analyzer:
    """
    X12 278 X215 QRE Analyzer
    
    Validates X12 278 inquiry transactions against QRE requirements.
    """
    
    def __init__(self, config_path: str = "qre-analyzer.config.json"):
        """Initialize analyzer with configuration"""
        with open(config_path, 'r') as f:
            self.config = json.load(f)
        
        self.results: List[ValidationResult] = []
        self.segments_found: List[str] = []
        
    def analyze_file(self, file_path: str) -> AnalysisReport:
        """Analyze an X12 278 EDI file"""
        print(f"Analyzing X12 278 file: {file_path}")
        
        try:
            with open(file_path, 'r') as f:
                content = f.read()
        except Exception as e:
            return self._create_error_report(file_path, f"Failed to read file: {str(e)}")
        
        # Parse X12 content
        self.results = []
        self.segments_found = []
        
        # Split by segment terminator
        segments = content.split('~')
        segments = [s.strip() for s in segments if s.strip()]
        
        # Extract segment identifiers
        for segment in segments:
            if segment:
                seg_id = segment.split('*')[0]
                self.segments_found.append(seg_id)
        
        # Run validations
        self._validate_envelopes(segments)
        self._validate_required_segments()
        self._validate_qre_requirements(segments)
        self._detect_query_method(segments)
        
        # Generate report
        return self._generate_report(file_path)
    
    def _validate_envelopes(self, segments: List[str]):
        """Validate ISA/GS/ST/SE/GE/IEA envelopes"""
        if not self.config['validationRules']['validateEnvelopes']:
            return
        
        # Check ISA
        isa_segments = [s for s in segments if s.startswith('ISA*')]
        if len(isa_segments) == 0:
            self.results.append(ValidationResult(
                severity=Severity.ERROR,
                code="ENV001",
                message="Missing ISA segment (Interchange Control Header)",
                segment="ISA"
            ))
        elif len(isa_segments) > 1:
            self.results.append(ValidationResult(
                severity=Severity.WARNING,
                code="ENV002",
                message=f"Multiple ISA segments found ({len(isa_segments)})",
                segment="ISA"
            ))
        
        # Check GS
        gs_segments = [s for s in segments if s.startswith('GS*')]
        if len(gs_segments) == 0:
            self.results.append(ValidationResult(
                severity=Severity.ERROR,
                code="ENV003",
                message="Missing GS segment (Functional Group Header)",
                segment="GS"
            ))
        
        # Check ST - must have transaction code 278
        st_segments = [s for s in segments if s.startswith('ST*')]
        if len(st_segments) == 0:
            self.results.append(ValidationResult(
                severity=Severity.ERROR,
                code="ENV004",
                message="Missing ST segment (Transaction Set Header)",
                segment="ST"
            ))
        else:
            st_elements = st_segments[0].split('*')
            if len(st_elements) >= 2 and st_elements[1] != '278':
                self.results.append(ValidationResult(
                    severity=Severity.ERROR,
                    code="ENV005",
                    message=f"Invalid transaction code: expected '278', found '{st_elements[1]}'",
                    segment="ST"
                ))
            if len(st_elements) >= 4 and not st_elements[3].endswith('X215'):
                self.results.append(ValidationResult(
                    severity=Severity.WARNING,
                    code="ENV006",
                    message=f"Implementation guide version '{st_elements[3]}' may not be 005010X215",
                    segment="ST",
                    context={"version": st_elements[3]}
                ))
    
    def _validate_required_segments(self):
        """Validate required X12 segments per QRE"""
        required = self.config['qreRequirements']['requiredSegments']
        
        for seg_id in required:
            if seg_id not in self.segments_found:
                self.results.append(ValidationResult(
                    severity=Severity.ERROR,
                    code="QRE001",
                    message=f"Missing required segment: {seg_id}",
                    segment=seg_id
                ))
    
    def _validate_qre_requirements(self, segments: List[str]):
        """Validate QRE-specific requirements (minimal data principle)"""
        if not self.config['qreRequirements']['minimalDataPrinciple']:
            return
        
        # Check for BHT segment with correct hierarchy code
        bht_segments = [s for s in segments if s.startswith('BHT*')]
        if bht_segments:
            bht_elements = bht_segments[0].split('*')
            if len(bht_elements) >= 2:
                if bht_elements[1] != '0007':
                    self.results.append(ValidationResult(
                        severity=Severity.WARNING,
                        code="QRE002",
                        message=f"BHT01 should be '0007' for inquiry, found '{bht_elements[1]}'",
                        segment="BHT",
                        context={"bht01": bht_elements[1]}
                    ))
        
        # Check for UM segment (service type)
        um_segments = [s for s in segments if s.startswith('UM*')]
        if not um_segments:
            self.results.append(ValidationResult(
                severity=Severity.WARNING,
                code="QRE003",
                message="UM segment (Health Care Services Review Information) is recommended for QRE",
                segment="UM"
            ))
        
        # Check for HCR segment (request for service type)
        hcr_segments = [s for s in segments if s.startswith('HCR*')]
        if hcr_segments:
            hcr_elements = hcr_segments[0].split('*')
            if len(hcr_elements) >= 2:
                # HCR01 should be 'I1' for inquiry
                if hcr_elements[1] not in ['I1', 'A1', 'A2', 'A3', 'A4']:
                    self.results.append(ValidationResult(
                        severity=Severity.INFO,
                        code="QRE004",
                        message=f"HCR01 action code is '{hcr_elements[1]}' (I1=Inquiry is recommended)",
                        segment="HCR",
                        context={"hcr01": hcr_elements[1]}
                    ))
    
    def _detect_query_method(self, segments: List[str]):
        """Detect query method (by auth number or member demographics)"""
        has_ref_auth = any(s.startswith('REF*D9*') for s in segments)
        has_member_id = any(s.startswith('NM1*IL*') for s in segments)
        has_dob = any(s.startswith('DMG*') for s in segments)
        
        if has_ref_auth:
            self.query_method = "ByAuthorizationNumber"
            self.results.append(ValidationResult(
                severity=Severity.INFO,
                code="QRE005",
                message="Query method: Authorization Number (REF*D9 segment found)",
                segment="REF"
            ))
        elif has_member_id and has_dob:
            self.query_method = "ByMemberDemographics"
            self.results.append(ValidationResult(
                severity=Severity.INFO,
                code="QRE006",
                message="Query method: Member Demographics (NM1*IL and DMG segments found)",
                segment="NM1"
            ))
        else:
            self.query_method = "Unknown"
            self.results.append(ValidationResult(
                severity=Severity.WARNING,
                code="QRE007",
                message="Cannot determine query method (need REF*D9 OR (NM1*IL + DMG))",
                segment="REF"
            ))
    
    def _generate_report(self, file_path: str) -> AnalysisReport:
        """Generate analysis report"""
        error_count = sum(1 for r in self.results if r.severity == Severity.ERROR)
        warning_count = sum(1 for r in self.results if r.severity == Severity.WARNING)
        info_count = sum(1 for r in self.results if r.severity == Severity.INFO)
        
        is_valid = error_count == 0
        if self.config['errorHandling']['failOnWarnings']:
            is_valid = is_valid and warning_count == 0
        
        return AnalysisReport(
            file_path=file_path,
            tr3_version=self.config['tr3Version'],
            is_valid=is_valid,
            error_count=error_count,
            warning_count=warning_count,
            info_count=info_count,
            results=self.results,
            query_method=getattr(self, 'query_method', None),
            segments_found=self.segments_found
        )
    
    def _create_error_report(self, file_path: str, error_msg: str) -> AnalysisReport:
        """Create error report for file read failures"""
        return AnalysisReport(
            file_path=file_path,
            tr3_version=self.config['tr3Version'],
            is_valid=False,
            error_count=1,
            warning_count=0,
            info_count=0,
            results=[ValidationResult(
                severity=Severity.ERROR,
                code="SYS001",
                message=error_msg
            )],
            segments_found=[]
        )
    
    def print_report(self, report: AnalysisReport):
        """Print analysis report to console"""
        print("\n" + "="*80)
        print(f"X12 278 X215 QRE Analysis Report")
        print("="*80)
        print(f"File: {report.file_path}")
        print(f"TR3 Version: {report.tr3_version}")
        print(f"Valid: {'✓ YES' if report.is_valid else '✗ NO'}")
        print(f"Errors: {report.error_count}")
        print(f"Warnings: {report.warning_count}")
        print(f"Info: {report.info_count}")
        if report.query_method:
            print(f"Query Method: {report.query_method}")
        print(f"Segments Found: {len(report.segments_found)}")
        print("-"*80)
        
        # Group results by severity
        for severity in [Severity.ERROR, Severity.WARNING, Severity.INFO]:
            severity_results = [r for r in report.results if r.severity == severity]
            if severity_results:
                print(f"\n{severity.value}S ({len(severity_results)}):")
                for result in severity_results:
                    seg_info = f" [{result.segment}]" if result.segment else ""
                    print(f"  {result.code}{seg_info}: {result.message}")
                    if result.context:
                        print(f"    Context: {result.context}")
        
        print("\n" + "="*80)
    
    def export_report_json(self, report: AnalysisReport, output_path: Optional[str] = None):
        """Export report as JSON"""
        report_dict = {
            "file_path": report.file_path,
            "tr3_version": report.tr3_version,
            "is_valid": report.is_valid,
            "error_count": report.error_count,
            "warning_count": report.warning_count,
            "info_count": report.info_count,
            "query_method": report.query_method,
            "segments_found": report.segments_found,
            "results": [
                {
                    "severity": r.severity.value,
                    "code": r.code,
                    "message": r.message,
                    "segment": r.segment,
                    "line_number": r.line_number,
                    "context": r.context
                }
                for r in report.results
            ]
        }
        
        if output_path:
            with open(output_path, 'w') as f:
                json.dump(report_dict, f, indent=2)
            print(f"\nReport exported to: {output_path}")
        else:
            print(json.dumps(report_dict, indent=2))


def main():
    """Main entry point"""
    if len(sys.argv) < 2:
        print("Usage: python x12_278_qre_analyzer.py <file.edi> [config.json]")
        print("\nExample:")
        print("  python x12_278_qre_analyzer.py sample-278-inquiry.edi")
        print("  python x12_278_qre_analyzer.py sample-278-inquiry.edi custom-config.json")
        sys.exit(1)
    
    file_path = sys.argv[1]
    config_path = sys.argv[2] if len(sys.argv) > 2 else "qre-analyzer.config.json"
    
    try:
        analyzer = X12_278_QRE_Analyzer(config_path)
        report = analyzer.analyze_file(file_path)
        analyzer.print_report(report)
        
        # Export JSON if configured
        if analyzer.config['outputOptions']['outputPath']:
            analyzer.export_report_json(report, analyzer.config['outputOptions']['outputPath'])
        
        # Exit with error code if validation failed
        sys.exit(0 if report.is_valid else 1)
        
    except Exception as e:
        print(f"Error: {str(e)}", file=sys.stderr)
        sys.exit(1)


if __name__ == "__main__":
    main()
