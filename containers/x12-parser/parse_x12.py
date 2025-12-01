#!/usr/bin/env python3
"""
X12 EDI Parser for Cloud Health Office

Parses HIPAA X12 EDI files (275, 277, 278) into structured JSON format.
Supports ISA/GS envelope extraction, transaction set parsing, and segment-level data extraction.

HIPAA Transaction Types:
- 005010X210 (275) - Additional Information to Support a Health Care Claim or Encounter
- 005010X212 (277) - Health Care Claim Status Notification  
- 005010X217 (278) - Health Care Services Review Information

Usage:
    python parse_x12.py input.edi [--output output.json] [--transaction-type 275|277|278]

Environment Variables:
    LOG_LEVEL: Logging level (DEBUG, INFO, WARNING, ERROR)
    OUTPUT_DIR: Directory for output files (default: /data/output)
"""

import argparse
import json
import logging
import os
import re
import sys
from dataclasses import dataclass, field, asdict
from datetime import datetime, timezone
from typing import List, Dict, Optional, Any
from enum import Enum


class TransactionType(Enum):
    """Supported X12 transaction types"""
    X12_275 = "275"  # Additional Information / Attachment
    X12_277 = "277"  # Claim Status Notification
    X12_278 = "278"  # Health Care Services Review


@dataclass
class ISAEnvelope:
    """ISA Interchange Control Header"""
    authorization_info_qualifier: str = ""  # ISA01
    authorization_info: str = ""            # ISA02
    security_info_qualifier: str = ""       # ISA03
    security_info: str = ""                 # ISA04
    sender_id_qualifier: str = ""           # ISA05
    sender_id: str = ""                     # ISA06
    receiver_id_qualifier: str = ""         # ISA07
    receiver_id: str = ""                   # ISA08
    interchange_date: str = ""              # ISA09
    interchange_time: str = ""              # ISA10
    repetition_separator: str = ""          # ISA11
    interchange_control_version: str = ""   # ISA12
    interchange_control_number: str = ""    # ISA13
    acknowledgment_requested: str = ""      # ISA14
    usage_indicator: str = ""               # ISA15
    component_separator: str = ""           # ISA16


@dataclass
class GSEnvelope:
    """GS Functional Group Header"""
    functional_id_code: str = ""           # GS01
    application_sender_code: str = ""      # GS02
    application_receiver_code: str = ""    # GS03
    date: str = ""                         # GS04
    time: str = ""                         # GS05
    group_control_number: str = ""         # GS06
    responsible_agency_code: str = ""      # GS07
    version_release_code: str = ""         # GS08


@dataclass
class Segment:
    """Generic X12 segment"""
    segment_id: str
    elements: List[str]
    raw: str = ""
    
    def get_element(self, index: int, default: str = "") -> str:
        """Get element by 0-based index, return default if not found"""
        if 0 <= index < len(self.elements):
            return self.elements[index]
        return default


@dataclass
class HierarchicalLoop:
    """HL (Hierarchical Level) loop structure"""
    hl_id: str = ""
    parent_id: str = ""
    level_code: str = ""
    child_code: str = ""
    segments: List[Segment] = field(default_factory=list)
    children: List['HierarchicalLoop'] = field(default_factory=list)


@dataclass
class TransactionSet:
    """ST/SE Transaction Set"""
    transaction_set_id: str = ""           # ST01
    transaction_set_control_number: str = "" # ST02
    implementation_guide_version: str = "" # ST03
    segments: List[Segment] = field(default_factory=list)
    hierarchical_loops: List[HierarchicalLoop] = field(default_factory=list)


@dataclass
class ParsedX12:
    """Complete parsed X12 document"""
    file_name: str = ""
    transaction_type: str = ""
    isa_envelope: Optional[ISAEnvelope] = None
    gs_envelope: Optional[GSEnvelope] = None
    transaction_sets: List[TransactionSet] = field(default_factory=list)
    parse_errors: List[str] = field(default_factory=list)
    parse_warnings: List[str] = field(default_factory=list)
    metadata: Dict[str, Any] = field(default_factory=dict)
    parsed_at: str = ""


class X12Parser:
    """
    X12 EDI Parser for HIPAA transactions
    
    Parses 275, 277, and 278 transaction types into structured JSON.
    """
    
    # Segment ID to description mapping for HIPAA transactions
    SEGMENT_DESCRIPTIONS = {
        "ISA": "Interchange Control Header",
        "GS": "Functional Group Header",
        "ST": "Transaction Set Header",
        "BHT": "Beginning of Hierarchical Transaction",
        "HL": "Hierarchical Level",
        "NM1": "Individual or Organizational Name",
        "N3": "Address Information",
        "N4": "Geographic Location",
        "REF": "Reference Identification",
        "DMG": "Demographic Information",
        "INS": "Insured Benefit",
        "DTP": "Date/Time Period",
        "TRN": "Trace",
        "UM": "Health Care Services Review Information",
        "HCR": "Health Care Services Review",
        "STC": "Status Information",
        "CLM": "Health Claim",
        "PWK": "Paperwork",
        "NTE": "Note/Special Instruction",
        "EQ": "Eligibility or Benefit Inquiry",
        "SE": "Transaction Set Trailer",
        "GE": "Functional Group Trailer",
        "IEA": "Interchange Control Trailer"
    }
    
    def __init__(self, log_level: str = "INFO"):
        """Initialize parser with logging configuration"""
        self.logger = logging.getLogger("X12Parser")
        self.logger.setLevel(getattr(logging, log_level.upper(), logging.INFO))
        
        if not self.logger.handlers:
            handler = logging.StreamHandler(sys.stdout)
            handler.setFormatter(logging.Formatter(
                '%(asctime)s - %(name)s - %(levelname)s - %(message)s'
            ))
            self.logger.addHandler(handler)
    
    def parse_file(self, file_path: str) -> ParsedX12:
        """Parse an X12 EDI file and return structured data"""
        self.logger.info(f"Parsing X12 file: {file_path}")
        
        result = ParsedX12(
            file_name=os.path.basename(file_path),
            parsed_at=datetime.now(timezone.utc).isoformat().replace("+00:00", "Z")
        )
        
        try:
            with open(file_path, 'r', encoding='utf-8') as f:
                content = f.read()
            
            result = self.parse_content(content, result)
            
        except FileNotFoundError:
            result.parse_errors.append(f"File not found: {file_path}")
            self.logger.error(f"File not found: {file_path}")
        except UnicodeDecodeError as e:
            result.parse_errors.append(f"Encoding error: {str(e)}")
            self.logger.error(f"Encoding error: {str(e)}")
        except Exception as e:
            result.parse_errors.append(f"Parse error: {str(e)}")
            self.logger.error(f"Parse error: {str(e)}")
        
        return result
    
    def parse_content(self, content: str, result: Optional[ParsedX12] = None) -> ParsedX12:
        """Parse X12 EDI content string"""
        if result is None:
            result = ParsedX12(parsed_at=datetime.now(timezone.utc).isoformat().replace("+00:00", "Z"))
        
        # Detect delimiters from ISA segment
        # ISA segment is exactly 106 characters, segment terminator follows at position 105
        if len(content) < 106:
            result.parse_errors.append("Content too short to contain valid ISA segment")
            return result
        
        element_separator = content[3]
        segment_terminator = content[105]
        
        # Handle newlines in segment terminator detection
        if segment_terminator in ('\r', '\n'):
            segment_terminator = '~'
        
        self.logger.debug(f"Detected delimiters - Element: '{element_separator}', Segment: '{segment_terminator}'")
        
        # Split content into segments
        segments = self._split_segments(content, segment_terminator, element_separator)
        
        if not segments:
            result.parse_errors.append("No segments found in content")
            return result
        
        # Parse ISA envelope
        isa_segments = [s for s in segments if s.segment_id == "ISA"]
        if isa_segments:
            result.isa_envelope = self._parse_isa(isa_segments[0])
        else:
            result.parse_errors.append("Missing ISA segment")
        
        # Parse GS envelope
        gs_segments = [s for s in segments if s.segment_id == "GS"]
        if gs_segments:
            result.gs_envelope = self._parse_gs(gs_segments[0])
        else:
            result.parse_errors.append("Missing GS segment")
        
        # Parse transaction sets
        result.transaction_sets = self._parse_transaction_sets(segments)
        
        # Determine transaction type
        if result.transaction_sets:
            st_id = result.transaction_sets[0].transaction_set_id
            result.transaction_type = st_id
            self.logger.info(f"Detected transaction type: {st_id}")
        
        # Extract metadata based on transaction type
        result.metadata = self._extract_metadata(result)
        
        return result
    
    def _split_segments(self, content: str, segment_terminator: str, element_separator: str) -> List[Segment]:
        """Split content into segment objects"""
        segments = []
        
        # Clean content and split by segment terminator
        raw_segments = content.split(segment_terminator)
        
        for raw in raw_segments:
            raw = raw.strip()
            if not raw:
                continue
            
            elements = raw.split(element_separator)
            if elements:
                segment_id = elements[0].strip()
                segments.append(Segment(
                    segment_id=segment_id,
                    elements=elements[1:] if len(elements) > 1 else [],
                    raw=raw
                ))
        
        return segments
    
    def _parse_isa(self, segment: Segment) -> ISAEnvelope:
        """Parse ISA segment into ISAEnvelope"""
        return ISAEnvelope(
            authorization_info_qualifier=segment.get_element(0),
            authorization_info=segment.get_element(1),
            security_info_qualifier=segment.get_element(2),
            security_info=segment.get_element(3),
            sender_id_qualifier=segment.get_element(4),
            sender_id=segment.get_element(5).strip(),
            receiver_id_qualifier=segment.get_element(6),
            receiver_id=segment.get_element(7).strip(),
            interchange_date=segment.get_element(8),
            interchange_time=segment.get_element(9),
            repetition_separator=segment.get_element(10),
            interchange_control_version=segment.get_element(11),
            interchange_control_number=segment.get_element(12),
            acknowledgment_requested=segment.get_element(13),
            usage_indicator=segment.get_element(14),
            component_separator=segment.get_element(15)
        )
    
    def _parse_gs(self, segment: Segment) -> GSEnvelope:
        """Parse GS segment into GSEnvelope"""
        return GSEnvelope(
            functional_id_code=segment.get_element(0),
            application_sender_code=segment.get_element(1),
            application_receiver_code=segment.get_element(2),
            date=segment.get_element(3),
            time=segment.get_element(4),
            group_control_number=segment.get_element(5),
            responsible_agency_code=segment.get_element(6),
            version_release_code=segment.get_element(7)
        )
    
    def _parse_transaction_sets(self, segments: List[Segment]) -> List[TransactionSet]:
        """Parse ST/SE transaction sets"""
        transaction_sets = []
        current_set = None
        
        for segment in segments:
            if segment.segment_id == "ST":
                # Start new transaction set
                current_set = TransactionSet(
                    transaction_set_id=segment.get_element(0),
                    transaction_set_control_number=segment.get_element(1),
                    implementation_guide_version=segment.get_element(2)
                )
            elif segment.segment_id == "SE":
                # End transaction set
                if current_set:
                    # Parse hierarchical loops
                    current_set.hierarchical_loops = self._parse_hierarchical_loops(current_set.segments)
                    transaction_sets.append(current_set)
                    current_set = None
            elif current_set:
                # Add segment to current transaction set
                current_set.segments.append(segment)
        
        return transaction_sets
    
    def _parse_hierarchical_loops(self, segments: List[Segment]) -> List[HierarchicalLoop]:
        """Parse HL loops from segments"""
        hl_dict: Dict[str, HierarchicalLoop] = {}
        root_loops = []
        current_hl = None
        
        for segment in segments:
            if segment.segment_id == "HL":
                hl_id = segment.get_element(0)
                parent_id = segment.get_element(1)
                level_code = segment.get_element(2)
                child_code = segment.get_element(3)
                
                current_hl = HierarchicalLoop(
                    hl_id=hl_id,
                    parent_id=parent_id,
                    level_code=level_code,
                    child_code=child_code
                )
                hl_dict[hl_id] = current_hl
                
                if parent_id and parent_id in hl_dict:
                    hl_dict[parent_id].children.append(current_hl)
                else:
                    root_loops.append(current_hl)
            elif current_hl:
                current_hl.segments.append(segment)
        
        return root_loops
    
    def _extract_metadata(self, parsed: ParsedX12) -> Dict[str, Any]:
        """Extract business metadata based on transaction type"""
        metadata: Dict[str, Any] = {
            "sender_id": parsed.isa_envelope.sender_id if parsed.isa_envelope else "",
            "receiver_id": parsed.isa_envelope.receiver_id if parsed.isa_envelope else "",
            "interchange_control_number": parsed.isa_envelope.interchange_control_number if parsed.isa_envelope else "",
            "interchange_date": parsed.isa_envelope.interchange_date if parsed.isa_envelope else "",
            "version": parsed.gs_envelope.version_release_code if parsed.gs_envelope else ""
        }
        
        if not parsed.transaction_sets:
            return metadata
        
        ts = parsed.transaction_sets[0]
        all_segments = ts.segments
        
        # Extract common fields
        for segment in all_segments:
            if segment.segment_id == "BHT":
                metadata["bht_transaction_set_purpose"] = segment.get_element(1)
                metadata["bht_reference_id"] = segment.get_element(2)
                metadata["bht_date"] = segment.get_element(3)
                metadata["bht_time"] = segment.get_element(4)
            elif segment.segment_id == "TRN":
                metadata["trace_type"] = segment.get_element(0)
                metadata["trace_number"] = segment.get_element(1)
                metadata["trace_assigning_entity"] = segment.get_element(2)
            elif segment.segment_id == "NM1":
                entity_code = segment.get_element(0)
                if entity_code == "IL":  # Insured/Subscriber
                    metadata["member_last_name"] = segment.get_element(2)
                    metadata["member_first_name"] = segment.get_element(3)
                    metadata["member_id"] = segment.get_element(8)
                elif entity_code == "PR":  # Payer
                    metadata["payer_name"] = segment.get_element(2)
                    metadata["payer_id"] = segment.get_element(8)
                elif entity_code == "1P":  # Provider
                    metadata["provider_name"] = segment.get_element(2)
                    metadata["provider_npi"] = segment.get_element(8)
            elif segment.segment_id == "REF":
                ref_type = segment.get_element(0)
                if ref_type == "D9":  # Claim Identifier
                    metadata["rfai_reference"] = segment.get_element(1)
                elif ref_type == "1K":  # Payor Claim Control Number
                    metadata["claim_number"] = segment.get_element(1)
                elif ref_type == "EJ":  # Patient Account Number
                    metadata["patient_account_number"] = segment.get_element(1)
                elif ref_type == "SY":  # Social Security Number
                    metadata["ssn"] = segment.get_element(1)
            elif segment.segment_id == "DTP":
                date_type = segment.get_element(0)
                if date_type == "472":  # Service Date
                    metadata["service_date"] = segment.get_element(2)
            elif segment.segment_id == "CLM":
                metadata["claim_submitter_id"] = segment.get_element(0)
                metadata["claim_amount"] = segment.get_element(1)
        
        # Transaction-specific extraction
        if parsed.transaction_type == "278":
            for segment in all_segments:
                if segment.segment_id == "UM":
                    metadata["um_request_category"] = segment.get_element(0)
                    metadata["um_certification_type"] = segment.get_element(1)
                elif segment.segment_id == "HCR":
                    metadata["review_action_code"] = segment.get_element(0)
                    metadata["review_reason_code"] = segment.get_element(1)
        
        elif parsed.transaction_type == "277":
            for segment in all_segments:
                if segment.segment_id == "STC":
                    stc01 = segment.get_element(0)
                    if ":" in stc01:
                        parts = stc01.split(":")
                        metadata["status_category_code"] = parts[0] if len(parts) > 0 else ""
                        metadata["status_code"] = parts[1] if len(parts) > 1 else ""
                        metadata["entity_identifier"] = parts[2] if len(parts) > 2 else ""
                    metadata["status_date"] = segment.get_element(1)
        
        elif parsed.transaction_type == "275":
            for segment in all_segments:
                if segment.segment_id == "PWK":
                    metadata["attachment_report_type"] = segment.get_element(0)
                    metadata["attachment_transmission_code"] = segment.get_element(1)
                    metadata["attachment_control_number"] = segment.get_element(5)
                elif segment.segment_id == "NTE":
                    note_type = segment.get_element(0)
                    if note_type == "ADD":
                        metadata["attachment_note"] = segment.get_element(1)
        
        return metadata
    
    def to_json(self, parsed: ParsedX12, indent: int = 2) -> str:
        """Convert parsed X12 to JSON string"""
        def convert(obj: Any) -> Any:
            if hasattr(obj, '__dataclass_fields__'):
                return asdict(obj)
            elif isinstance(obj, list):
                return [convert(item) for item in obj]
            elif isinstance(obj, dict):
                return {k: convert(v) for k, v in obj.items()}
            return obj
        
        return json.dumps(convert(parsed), indent=indent)


def main():
    """Main entry point for X12 parser"""
    parser = argparse.ArgumentParser(
        description="Parse HIPAA X12 EDI files (275, 277, 278) to JSON"
    )
    parser.add_argument(
        "input_file",
        help="Path to X12 EDI file to parse"
    )
    parser.add_argument(
        "-o", "--output",
        help="Output JSON file path (default: stdout)"
    )
    parser.add_argument(
        "-t", "--transaction-type",
        choices=["275", "277", "278"],
        help="Expected transaction type (auto-detected if not specified)"
    )
    parser.add_argument(
        "-l", "--log-level",
        default=os.environ.get("LOG_LEVEL", "INFO"),
        choices=["DEBUG", "INFO", "WARNING", "ERROR"],
        help="Logging level"
    )
    parser.add_argument(
        "--metadata-only",
        action="store_true",
        help="Output only metadata (excludes raw segments)"
    )
    
    args = parser.parse_args()
    
    # Initialize parser
    x12_parser = X12Parser(log_level=args.log_level)
    
    # Parse file
    result = x12_parser.parse_file(args.input_file)
    
    # Validate transaction type if specified
    if args.transaction_type and result.transaction_type != args.transaction_type:
        print(f"Warning: Expected transaction type {args.transaction_type}, "
              f"but found {result.transaction_type}", file=sys.stderr)
    
    # Generate output
    if args.metadata_only:
        output = json.dumps({
            "file_name": result.file_name,
            "transaction_type": result.transaction_type,
            "metadata": result.metadata,
            "parse_errors": result.parse_errors,
            "parse_warnings": result.parse_warnings,
            "parsed_at": result.parsed_at
        }, indent=2)
    else:
        output = x12_parser.to_json(result)
    
    # Write output
    if args.output:
        with open(args.output, 'w') as f:
            f.write(output)
        print(f"Output written to: {args.output}", file=sys.stderr)
    else:
        print(output)
    
    # Exit with error code if parsing failed
    if result.parse_errors:
        sys.exit(1)
    
    sys.exit(0)


if __name__ == "__main__":
    main()
