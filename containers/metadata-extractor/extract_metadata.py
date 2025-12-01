#!/usr/bin/env python3
"""
Metadata Extractor for Cloud Health Office

Extracts business metadata from parsed X12 JSON for downstream processing.
Produces a standardized metadata structure for Kafka publishing and backend API calls.

Extracted Fields:
- Claim number
- Member ID, name, date of birth
- Provider NPI, name
- Payer ID, name
- Service dates
- Diagnosis codes (ICD-10)
- Procedure codes (CPT/HCPCS)
- Attachment metadata
- Authorization details

Usage:
    python extract_metadata.py --input parsed.json --output metadata.json
    cat parsed.json | python extract_metadata.py --stdin

Environment Variables:
    LOG_LEVEL: Logging level (DEBUG, INFO, WARNING, ERROR)
"""

import argparse
import json
import logging
import os
import sys
from dataclasses import dataclass, field, asdict
from datetime import datetime, timezone
from typing import List, Dict, Optional, Any


@dataclass
class MemberInfo:
    """Member/patient information"""
    member_id: str = ""
    first_name: str = ""
    last_name: str = ""
    middle_name: str = ""
    date_of_birth: str = ""
    gender: str = ""
    ssn: str = ""
    group_number: str = ""


@dataclass
class ProviderInfo:
    """Provider information"""
    npi: str = ""
    name: str = ""
    specialty: str = ""
    tax_id: str = ""


@dataclass
class PayerInfo:
    """Payer information"""
    payer_id: str = ""
    name: str = ""


@dataclass
class ServiceInfo:
    """Service/claim information"""
    claim_number: str = ""
    service_date_from: str = ""
    service_date_to: str = ""
    service_type: str = ""
    claim_amount: str = ""
    diagnosis_codes: List[str] = field(default_factory=list)
    procedure_codes: List[str] = field(default_factory=list)


@dataclass
class AttachmentInfo:
    """Attachment metadata"""
    attachment_control_number: str = ""
    report_type: str = ""
    transmission_code: str = ""
    rfai_reference: str = ""
    note: str = ""


@dataclass
class AuthorizationInfo:
    """Authorization/review information"""
    auth_number: str = ""
    review_action_code: str = ""
    review_reason_code: str = ""
    request_category: str = ""
    certification_type: str = ""


@dataclass
class ExtractedMetadata:
    """Complete extracted metadata"""
    # Document info
    file_name: str = ""
    transaction_type: str = ""
    implementation_version: str = ""
    
    # Interchange info
    sender_id: str = ""
    receiver_id: str = ""
    interchange_control_number: str = ""
    interchange_date: str = ""
    
    # Trace info
    trace_number: str = ""
    bht_reference_id: str = ""
    
    # Business entities
    member: MemberInfo = field(default_factory=MemberInfo)
    provider: ProviderInfo = field(default_factory=ProviderInfo)
    payer: PayerInfo = field(default_factory=PayerInfo)
    
    # Service details
    service: ServiceInfo = field(default_factory=ServiceInfo)
    attachment: AttachmentInfo = field(default_factory=AttachmentInfo)
    authorization: AuthorizationInfo = field(default_factory=AuthorizationInfo)
    
    # Processing metadata
    extracted_at: str = ""
    extraction_version: str = "1.0.0"


class MetadataExtractor:
    """
    Metadata Extractor for parsed X12 JSON
    
    Extracts standardized business metadata from the output of the X12 parser.
    """
    
    # Report type code descriptions
    REPORT_TYPES = {
        "OZ": "Support Data for Claim",
        "OC": "Operative Note",
        "PH": "Physician Report",
        "LA": "Lab Results",
        "DG": "Diagnostic Report",
        "RB": "Radiology Films",
        "B3": "Wellness Report"
    }
    
    # Review action code descriptions
    REVIEW_ACTIONS = {
        "A1": "Certified in Total",
        "A2": "Certified - Partial",
        "A3": "Not Certified",
        "A4": "Pended",
        "A5": "Cancelled",
        "A6": "Modified"
    }
    
    def __init__(self, log_level: str = "INFO"):
        """Initialize extractor with logging"""
        self.logger = logging.getLogger("MetadataExtractor")
        self.logger.setLevel(getattr(logging, log_level.upper(), logging.INFO))
        
        if not self.logger.handlers:
            handler = logging.StreamHandler(sys.stdout)
            handler.setFormatter(logging.Formatter(
                '%(asctime)s - %(name)s - %(levelname)s - %(message)s'
            ))
            self.logger.addHandler(handler)
    
    def extract(self, parsed_json: Dict[str, Any]) -> ExtractedMetadata:
        """Extract metadata from parsed X12 JSON"""
        self.logger.info("Extracting metadata from parsed X12")
        
        metadata = ExtractedMetadata(
            extracted_at=datetime.now(timezone.utc).isoformat().replace("+00:00", "Z")
        )
        
        # Extract document info
        metadata.file_name = parsed_json.get("file_name", "")
        metadata.transaction_type = parsed_json.get("transaction_type", "")
        
        # Extract ISA envelope info
        isa = parsed_json.get("isa_envelope", {})
        if isa:
            metadata.sender_id = isa.get("sender_id", "")
            metadata.receiver_id = isa.get("receiver_id", "")
            metadata.interchange_control_number = isa.get("interchange_control_number", "")
            metadata.interchange_date = isa.get("interchange_date", "")
        
        # Extract GS envelope info
        gs = parsed_json.get("gs_envelope", {})
        if gs:
            metadata.implementation_version = gs.get("version_release_code", "")
        
        # Extract from metadata section (already extracted by parser)
        source_metadata = parsed_json.get("metadata", {})
        self._extract_from_metadata(metadata, source_metadata)
        
        # Extract from transaction sets and segments
        transaction_sets = parsed_json.get("transaction_sets", [])
        if transaction_sets:
            self._extract_from_transaction_set(metadata, transaction_sets[0])
        
        self.logger.info(f"Extracted metadata for claim: {metadata.service.claim_number}")
        return metadata
    
    def _extract_from_metadata(self, metadata: ExtractedMetadata, source: Dict[str, Any]):
        """Extract from pre-parsed metadata"""
        # Member info
        metadata.member.member_id = source.get("member_id", "")
        metadata.member.first_name = source.get("member_first_name", "")
        metadata.member.last_name = source.get("member_last_name", "")
        metadata.member.ssn = source.get("ssn", "")
        
        # Provider info
        metadata.provider.npi = source.get("provider_npi", "")
        metadata.provider.name = source.get("provider_name", "")
        
        # Payer info
        metadata.payer.payer_id = source.get("payer_id", "")
        metadata.payer.name = source.get("payer_name", "")
        
        # Service info
        metadata.service.claim_number = source.get("claim_number", "") or source.get("claim_submitter_id", "")
        metadata.service.service_date_from = source.get("service_date", "")
        metadata.service.claim_amount = source.get("claim_amount", "")
        
        # Attachment info
        metadata.attachment.rfai_reference = source.get("rfai_reference", "")
        metadata.attachment.attachment_control_number = source.get("attachment_control_number", "")
        metadata.attachment.report_type = source.get("attachment_report_type", "")
        metadata.attachment.transmission_code = source.get("attachment_transmission_code", "")
        metadata.attachment.note = source.get("attachment_note", "")
        
        # Authorization info (278)
        metadata.authorization.review_action_code = source.get("review_action_code", "")
        metadata.authorization.review_reason_code = source.get("review_reason_code", "")
        metadata.authorization.request_category = source.get("um_request_category", "")
        metadata.authorization.certification_type = source.get("um_certification_type", "")
        
        # Trace info
        metadata.trace_number = source.get("trace_number", "")
        metadata.bht_reference_id = source.get("bht_reference_id", "")
    
    def _extract_from_transaction_set(self, metadata: ExtractedMetadata, ts: Dict[str, Any]):
        """Extract from transaction set segments"""
        segments = ts.get("segments", [])
        
        for segment in segments:
            segment_id = segment.get("segment_id", "")
            elements = segment.get("elements", [])
            
            if segment_id == "DMG" and len(elements) >= 2:
                # Demographics
                metadata.member.date_of_birth = elements[1] if len(elements) > 1 else ""
                metadata.member.gender = elements[2] if len(elements) > 2 else ""
            
            elif segment_id == "HI" and elements:
                # Health Care Diagnosis Codes
                for elem in elements:
                    if ":" in elem:
                        parts = elem.split(":")
                        code_qualifier = parts[0] if len(parts) > 0 else ""
                        code = parts[1] if len(parts) > 1 else ""
                        
                        if code_qualifier in ("ABK", "ABF"):  # ICD-10-CM Principal/Other
                            metadata.service.diagnosis_codes.append(code)
                        elif code_qualifier in ("BBR", "BBQ"):  # CPT/HCPCS Procedure
                            metadata.service.procedure_codes.append(code)
            
            elif segment_id == "DTP" and len(elements) >= 3:
                date_qualifier = elements[0]
                date_format = elements[1]
                date_value = elements[2]
                
                if date_qualifier == "472":  # Service
                    if date_format == "RD8" and "-" in date_value:
                        parts = date_value.split("-")
                        metadata.service.service_date_from = parts[0]
                        metadata.service.service_date_to = parts[1] if len(parts) > 1 else ""
                    else:
                        metadata.service.service_date_from = date_value
            
            elif segment_id == "TRN" and len(elements) >= 2:
                metadata.trace_number = elements[1]
            
            elif segment_id == "REF" and len(elements) >= 2:
                ref_qualifier = elements[0]
                ref_value = elements[1]
                
                if ref_qualifier == "G1":  # Prior Auth Number
                    metadata.authorization.auth_number = ref_value
                elif ref_qualifier == "1K" and not metadata.service.claim_number:
                    metadata.service.claim_number = ref_value
        
        # Also check hierarchical loops
        hl_loops = ts.get("hierarchical_loops", [])
        self._extract_from_hl_loops(metadata, hl_loops)
    
    def _extract_from_hl_loops(self, metadata: ExtractedMetadata, loops: List[Dict[str, Any]]):
        """Extract from hierarchical loops recursively"""
        for loop in loops:
            level_code = loop.get("level_code", "")
            segments = loop.get("segments", [])
            children = loop.get("children", [])
            
            # Process segments in this loop
            for segment in segments:
                segment_id = segment.get("segment_id", "")
                elements = segment.get("elements", [])
                
                if segment_id == "NM1" and len(elements) >= 9:
                    entity_code = elements[0]
                    
                    if entity_code == "IL":  # Insured/Subscriber
                        metadata.member.last_name = elements[2] if len(elements) > 2 else ""
                        metadata.member.first_name = elements[3] if len(elements) > 3 else ""
                        metadata.member.middle_name = elements[4] if len(elements) > 4 else ""
                        metadata.member.member_id = elements[8] if len(elements) > 8 else ""
                    elif entity_code == "1P":  # Provider
                        metadata.provider.name = elements[2] if len(elements) > 2 else ""
                        metadata.provider.npi = elements[8] if len(elements) > 8 else ""
                    elif entity_code == "PR":  # Payer
                        metadata.payer.name = elements[2] if len(elements) > 2 else ""
                        metadata.payer.payer_id = elements[8] if len(elements) > 8 else ""
            
            # Recursively process children
            if children:
                self._extract_from_hl_loops(metadata, children)
    
    def to_dict(self, metadata: ExtractedMetadata) -> Dict[str, Any]:
        """Convert metadata to dictionary"""
        return asdict(metadata)
    
    def to_json(self, metadata: ExtractedMetadata, indent: int = 2) -> str:
        """Convert metadata to JSON string"""
        return json.dumps(self.to_dict(metadata), indent=indent)
    
    def to_flat_dict(self, metadata: ExtractedMetadata) -> Dict[str, Any]:
        """Convert to flattened dictionary (useful for Kafka headers)"""
        d = self.to_dict(metadata)
        flat = {}
        
        def flatten(obj: Any, prefix: str = ""):
            if isinstance(obj, dict):
                for k, v in obj.items():
                    key = f"{prefix}.{k}" if prefix else k
                    flatten(v, key)
            elif isinstance(obj, list):
                flat[prefix] = ",".join(str(x) for x in obj)
            else:
                flat[prefix] = str(obj) if obj else ""
        
        flatten(d)
        return flat


def main():
    """Main entry point for metadata extractor"""
    parser = argparse.ArgumentParser(
        description="Extract business metadata from parsed X12 JSON"
    )
    
    # Input options
    parser.add_argument("-i", "--input", help="Input parsed X12 JSON file")
    parser.add_argument("--stdin", action="store_true", help="Read JSON from stdin")
    
    # Output options
    parser.add_argument("-o", "--output", help="Output metadata JSON file (default: stdout)")
    parser.add_argument("--flat", action="store_true", help="Output flattened key-value pairs")
    parser.add_argument("--compact", action="store_true", help="Output compact JSON (no indentation)")
    
    # Logging
    parser.add_argument("-l", "--log-level", default=os.environ.get("LOG_LEVEL", "INFO"),
                       choices=["DEBUG", "INFO", "WARNING", "ERROR"], help="Logging level")
    
    args = parser.parse_args()
    
    # Validate input
    if not args.input and not args.stdin:
        print("Error: Must specify --input or --stdin", file=sys.stderr)
        sys.exit(1)
    
    # Initialize extractor
    extractor = MetadataExtractor(log_level=args.log_level)
    
    # Read input
    try:
        if args.stdin:
            input_data = json.load(sys.stdin)
        else:
            with open(args.input, 'r') as f:
                input_data = json.load(f)
    except json.JSONDecodeError as e:
        print(f"Error: Invalid JSON input: {str(e)}", file=sys.stderr)
        sys.exit(1)
    except FileNotFoundError:
        print(f"Error: Input file not found: {args.input}", file=sys.stderr)
        sys.exit(1)
    
    # Extract metadata
    metadata = extractor.extract(input_data)
    
    # Generate output
    if args.flat:
        output = json.dumps(extractor.to_flat_dict(metadata), indent=None if args.compact else 2)
    else:
        indent = None if args.compact else 2
        output = extractor.to_json(metadata, indent=indent if indent else 0)
    
    # Write output
    if args.output:
        with open(args.output, 'w') as f:
            f.write(output)
        print(f"Metadata written to: {args.output}", file=sys.stderr)
    else:
        print(output)
    
    sys.exit(0)


if __name__ == "__main__":
    main()
