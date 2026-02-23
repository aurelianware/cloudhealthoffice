#!/usr/bin/env python3
"""
X12 276 Claim Status Inquiry Parser
Parses HIPAA X12 276 (005010X212) transactions into structured JSON

HIPAA Transaction: 005010X212 - Health Care Claim Status Request

Usage:
    python parse-276.py input.edi --output output.json

Input: X12 276 EDI file
Output: JSON structure with claim status inquiry details
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
class ISAEnvelope:
    """ISA Interchange Control Header"""
    sender_id: str = ""
    receiver_id: str = ""
    interchange_date: str = ""
    interchange_time: str = ""
    interchange_control_number: str = ""
    usage_indicator: str = ""


@dataclass
class Provider:
    """Provider information (NM1 segment with qualifier PR, FA, etc.)"""
    entity_type: str = ""  # 1=Person, 2=Non-Person
    last_name_or_org: str = ""
    first_name: str = ""
    middle_name: str = ""
    name_suffix: str = ""
    id_code_qualifier: str = ""  # XX=NPI, etc.
    id_code: str = ""
    entity_identifier: str = ""  # PR=Payer, FA=Facility, etc.


@dataclass
class Patient:
    """Patient/Subscriber information"""
    entity_type: str = ""
    last_name: str = ""
    first_name: str = ""
    middle_name: str = ""
    member_id: str = ""
    date_of_birth: str = ""


@dataclass
class ClaimInquiry:
    """Individual claim status inquiry (2200D loop)"""
    claim_number: str = ""
    patient: Optional[Patient] = None
    provider: Optional[Provider] = None
    service_date_from: str = ""
    service_date_to: str = ""
    total_claim_charge: str = ""
    trace_number: str = ""


@dataclass
class StatusInquiry:
    """276 Status Inquiry transaction"""
    transaction_set_control_number: str = ""
    information_source: Optional[Provider] = None  # Payer
    information_receiver: Optional[Provider] = None  # Provider/Submitter
    subscriber: Optional[Patient] = None
    claims: List[ClaimInquiry] = field(default_factory=list)
    trace_numbers: List[str] = field(default_factory=list)


@dataclass
class Parsed276:
    """Complete parsed 276 document"""
    file_name: str = ""
    isa_envelope: Optional[ISAEnvelope] = None
    inquiries: List[StatusInquiry] = field(default_factory=list)
    parse_errors: List[str] = field(default_factory=list)
    parsed_at: str = ""


class X12_276_Parser:
    """Parser for X12 276 Claim Status Inquiry transactions"""
    
    def __init__(self, log_level: str = "INFO"):
        self.logger = logging.getLogger("X12_276_Parser")
        self.logger.setLevel(getattr(logging, log_level.upper(), logging.INFO))
        
        if not self.logger.handlers:
            handler = logging.StreamHandler(sys.stdout)
            handler.setFormatter(logging.Formatter(
                '%(asctime)s - %(levelname)s - %(message)s'
            ))
            self.logger.addHandler(handler)
    
    def parse_file(self, file_path: str) -> Parsed276:
        """Parse X12 276 EDI file"""
        self.logger.info(f"Parsing 276 file: {file_path}")
        
        result = Parsed276(
            file_name=os.path.basename(file_path),
            parsed_at=datetime.now(timezone.utc).isoformat().replace("+00:00", "Z")
        )
        
        try:
            with open(file_path, 'r', encoding='utf-8') as f:
                content = f.read()
            
            # Split into segments
            segments = self._split_segments(content)
            self._parse_segments(segments, result)
            
        except FileNotFoundError:
            result.parse_errors.append(f"File not found: {file_path}")
            self.logger.error(f"File not found: {file_path}")
        except Exception as e:
            result.parse_errors.append(f"Parse error: {str(e)}")
            self.logger.error(f"Parse error: {str(e)}", exc_info=True)
        
        return result
    
    def _split_segments(self, content: str) -> List[List[str]]:
        """Split EDI content into segments and elements"""
        # Remove whitespace and newlines
        content = content.replace('\n', '').replace('\r', '')
        
        # Split by segment terminator (~)
        segment_strings = [s.strip() for s in content.split('~') if s.strip()]
        
        # Split each segment by element separator (*)
        segments = []
        for seg_str in segment_strings:
            elements = seg_str.split('*')
            if elements:
                segments.append(elements)
        
        return segments
    
    def _parse_segments(self, segments: List[List[str]], result: Parsed276):
        """Parse segments into structured data"""
        current_inquiry = None
        current_claim = None
        current_entity = None
        
        for seg in segments:
            if not seg:
                continue
            
            seg_id = seg[0]
            
            try:
                if seg_id == "ISA":
                    result.isa_envelope = self._parse_isa(seg)
                
                elif seg_id == "ST":
                    # Start new transaction
                    current_inquiry = StatusInquiry()
                    current_inquiry.transaction_set_control_number = seg[2] if len(seg) > 2 else ""
                    result.inquiries.append(current_inquiry)
                
                elif seg_id == "TRN" and current_inquiry:
                    # Trace number
                    if len(seg) > 2:
                        current_inquiry.trace_numbers.append(seg[2])
                
                elif seg_id == "NM1" and current_inquiry:
                    # Name/Entity
                    entity = self._parse_nm1(seg)
                    current_entity = entity
                    
                    # Determine entity type
                    entity_id = seg[1] if len(seg) > 1 else ""
                    
                    if entity_id == "PR":  # Payer (Information Source)
                        current_inquiry.information_source = entity
                    elif entity_id in ["1P", "FA", "71"]:  # Provider/Submitter
                        current_inquiry.information_receiver = entity
                    elif entity_id == "IL":  # Subscriber/Insured
                        current_inquiry.subscriber = self._nm1_to_patient(entity, seg)
                    elif entity_id == "QC":  # Patient (if different from subscriber)
                        if current_claim:
                            current_claim.patient = self._nm1_to_patient(entity, seg)
                
                elif seg_id == "REF" and current_entity:
                    # Reference ID (often member ID, NPI, etc.)
                    if len(seg) > 2 and current_inquiry and current_inquiry.subscriber:
                        ref_qualifier = seg[1] if len(seg) > 1 else ""
                        if ref_qualifier == "0F":  # Member ID
                            current_inquiry.subscriber.member_id = seg[2]
                
                elif seg_id == "DMG" and current_inquiry and current_inquiry.subscriber:
                    # Demographics (date of birth)
                    if len(seg) > 2:
                        current_inquiry.subscriber.date_of_birth = seg[2]
                
                elif seg_id == "HL":
                    # Hierarchical Level - might indicate start of claim loop
                    level_code = seg[3] if len(seg) > 3 else ""
                    if level_code == "PT":  # Patient level
                        current_claim = ClaimInquiry()
                        if current_inquiry:
                            current_inquiry.claims.append(current_claim)
                
                elif seg_id == "TRN" and current_claim:
                    # Trace number for specific claim
                    if len(seg) > 2:
                        current_claim.trace_number = seg[2]
                
                elif seg_id == "REF" and current_claim:
                    # Claim reference
                    ref_qualifier = seg[1] if len(seg) > 1 else ""
                    if ref_qualifier == "D9":  # Claim number
                        current_claim.claim_number = seg[2] if len(seg) > 2 else ""
                
                elif seg_id == "DTP" and current_claim:
                    # Date range for claim
                    date_qualifier = seg[1] if len(seg) > 1 else ""
                    if date_qualifier == "472":  # Service date
                        if len(seg) > 3:
                            date_range = seg[3]
                            if "-" in date_range:
                                dates = date_range.split("-")
                                current_claim.service_date_from = dates[0]
                                current_claim.service_date_to = dates[1] if len(dates) > 1 else dates[0]
                            else:
                                current_claim.service_date_from = date_range
                                current_claim.service_date_to = date_range
                
                elif seg_id == "AMT" and current_claim:
                    # Claim amount
                    amount_qualifier = seg[1] if len(seg) > 1 else ""
                    if amount_qualifier == "T3":  # Total claim charge
                        current_claim.total_claim_charge = seg[2] if len(seg) > 2 else ""
            
            except Exception as e:
                result.parse_errors.append(f"Error parsing segment {seg_id}: {str(e)}")
                self.logger.warning(f"Error parsing segment {seg_id}: {str(e)}")
    
    def _parse_isa(self, seg: List[str]) -> ISAEnvelope:
        """Parse ISA segment"""
        return ISAEnvelope(
            sender_id=seg[6].strip() if len(seg) > 6 else "",
            receiver_id=seg[8].strip() if len(seg) > 8 else "",
            interchange_date=seg[9] if len(seg) > 9 else "",
            interchange_time=seg[10] if len(seg) > 10 else "",
            interchange_control_number=seg[13] if len(seg) > 13 else "",
            usage_indicator=seg[15] if len(seg) > 15 else ""
        )
    
    def _parse_nm1(self, seg: List[str]) -> Provider:
        """Parse NM1 segment into Provider"""
        return Provider(
            entity_identifier=seg[1] if len(seg) > 1 else "",
            entity_type=seg[2] if len(seg) > 2 else "",
            last_name_or_org=seg[3] if len(seg) > 3 else "",
            first_name=seg[4] if len(seg) > 4 else "",
            middle_name=seg[5] if len(seg) > 5 else "",
            name_suffix=seg[6] if len(seg) > 6 else "",
            id_code_qualifier=seg[8] if len(seg) > 8 else "",
            id_code=seg[9] if len(seg) > 9 else ""
        )
    
    def _nm1_to_patient(self, provider: Provider, seg: List[str]) -> Patient:
        """Convert NM1-based Provider to Patient structure"""
        return Patient(
            entity_type=provider.entity_type,
            last_name=provider.last_name_or_org,
            first_name=provider.first_name,
            middle_name=provider.middle_name,
            member_id=provider.id_code
        )


def main():
    """CLI entry point"""
    parser = argparse.ArgumentParser(description="Parse X12 276 Claim Status Inquiry")
    parser.add_argument("input", help="Input 276 EDI file path")
    parser.add_argument("--output", help="Output JSON file path (default: stdout)")
    parser.add_argument("--log-level", default="INFO", choices=["DEBUG", "INFO", "WARNING", "ERROR"])
    
    args = parser.parse_args()
    
    # Parse 276 file
    parser_instance = X12_276_Parser(log_level=args.log_level)
    result = parser_instance.parse_file(args.input)
    
    # Convert to dict for JSON serialization
    output_dict = asdict(result)
    
    # Write output
    if args.output:
        with open(args.output, 'w') as f:
            json.dump(output_dict, f, indent=2)
        print(f"Parsed 276 written to: {args.output}")
    else:
        print(json.dumps(output_dict, indent=2))
    
    # Exit with error code if parse errors
    if result.parse_errors:
        sys.exit(1)


if __name__ == "__main__":
    main()
