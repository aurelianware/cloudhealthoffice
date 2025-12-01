#!/usr/bin/env python3
"""
X12 277 EDI Generator for Cloud Health Office

Generates HIPAA X12 277 (Health Care Claim Status Notification) EDI files
from structured JSON input. Supports RFAI (Request for Additional Information)
responses to trading partners.

HIPAA Transaction Type: 005010X212 (277)

Usage:
    python generate_277.py --input claim.json --output 277.edi
    python generate_277.py --claim-number CLM123 --member-id MBR456 --rfai-reason A1

Environment Variables:
    LOG_LEVEL: Logging level (DEBUG, INFO, WARNING, ERROR)
    SENDER_ID: Default sender identifier
    RECEIVER_ID: Default receiver identifier
"""

import argparse
import json
import logging
import os
import sys
from dataclasses import dataclass, field
from datetime import datetime, timezone
from typing import Dict, Optional, Any, Tuple


@dataclass
class TradingPartner:
    """Trading partner configuration"""
    qualifier: str = "ZZ"
    identifier: str = ""
    name: str = ""


@dataclass
class X277Envelope:
    """X12 277 envelope configuration"""
    sender: TradingPartner = field(default_factory=TradingPartner)
    receiver: TradingPartner = field(default_factory=TradingPartner)
    interchange_control_number: str = ""
    group_control_number: str = ""
    transaction_set_control_number: str = ""
    implementation_guide_version: str = "005010X212"
    usage_indicator: str = "T"  # T=Test, P=Production


@dataclass
class ClaimStatusInfo:
    """Claim status information for 277 generation"""
    claim_number: str = ""
    member_id: str = ""
    member_first_name: str = ""
    member_last_name: str = ""
    provider_npi: str = ""
    provider_name: str = ""
    payer_id: str = ""
    payer_name: str = ""
    service_date: str = ""
    rfai_reference: str = ""
    rfai_reason_code: str = "A1"  # A1=Request for Additional Information
    status_category_code: str = "A1"
    status_code: str = "20"  # Additional Information Requested
    entity_identifier: str = "41"


class X12_277_Generator:
    """
    X12 277 EDI Generator
    
    Generates HIPAA-compliant 277 transactions for claim status notification
    and RFAI (Request for Additional Information) responses.
    """
    
    # Status category codes
    STATUS_CATEGORIES = {
        "A0": "Acknowledgement/Forwarded",
        "A1": "Request for Additional Information",
        "A2": "Accepted",
        "A3": "Rejected",
        "A4": "Not Found",
        "A5": "Split Claim",
        "A6": "Additional Claim Info Requested",
        "A7": "Suspended",
        "A8": "Search"
    }
    
    # RFAI reason codes (STC01-2)
    RFAI_REASON_CODES = {
        "20": "Additional Information Requested - Medical Records",
        "21": "Additional Information Requested - Prior Auth",
        "41": "Entity - Prior Authorization Required",
        "42": "Entity - Attachment Required",
        "43": "Entity - Specific Item Required",
        "256": "Service Requires Prior Authorization"
    }
    
    def __init__(self, log_level: str = "INFO"):
        """Initialize generator with logging configuration"""
        self.logger = logging.getLogger("X12_277_Generator")
        self.logger.setLevel(getattr(logging, log_level.upper(), logging.INFO))
        
        if not self.logger.handlers:
            handler = logging.StreamHandler(sys.stdout)
            handler.setFormatter(logging.Formatter(
                '%(asctime)s - %(name)s - %(levelname)s - %(message)s'
            ))
            self.logger.addHandler(handler)
        
        # Control number counters (in production, these should be persisted)
        self._isa_counter = 1
        self._gs_counter = 1
        self._st_counter = 1
    
    def generate(self, claim_info: ClaimStatusInfo, envelope: X277Envelope) -> str:
        """Generate X12 277 EDI from claim status information"""
        self.logger.info(f"Generating 277 for claim: {claim_info.claim_number}")
        
        segments = []
        segment_count = 0
        
        # Get current timestamp
        now = datetime.now(timezone.utc)
        isa_date = now.strftime("%y%m%d")
        isa_time = now.strftime("%H%M")
        gs_date = now.strftime("%Y%m%d")
        gs_time = now.strftime("%H%M")
        
        # Generate control numbers if not provided
        isa_control = envelope.interchange_control_number or self._format_control_number(self._isa_counter, 9)
        gs_control = envelope.group_control_number or str(self._gs_counter)
        st_control = envelope.transaction_set_control_number or f"{self._st_counter:04d}"
        
        # ISA - Interchange Control Header
        isa = self._build_isa(envelope, isa_date, isa_time, isa_control)
        segments.append(isa)
        
        # GS - Functional Group Header
        gs = self._build_gs(envelope, gs_date, gs_time, gs_control)
        segments.append(gs)
        
        # ST - Transaction Set Header
        segments.append(f"ST*277*{st_control}*{envelope.implementation_guide_version}")
        segment_count += 1
        
        # BHT - Beginning of Hierarchical Transaction
        bht_ref = claim_info.rfai_reference or f"RFAI{now.strftime('%Y%m%d%H%M%S')}"
        segments.append(f"BHT*0010*08*{bht_ref}*{gs_date}*{gs_time}")
        segment_count += 1
        
        # HL*1 - Information Source Level (Payer)
        segments.append("HL*1**20*1")
        segment_count += 1
        
        # NM1 - Information Source Name (Payer)
        payer_name = claim_info.payer_name or "HEALTH PLAN"
        payer_id = claim_info.payer_id or envelope.sender.identifier
        segments.append(f"NM1*PR*2*{payer_name}*****PI*{payer_id}")
        segment_count += 1
        
        # HL*2 - Information Receiver Level (Subscriber)
        segments.append("HL*2*1*22*1")
        segment_count += 1
        
        # NM1 - Subscriber Name
        segments.append(f"NM1*IL*1*{claim_info.member_last_name}*{claim_info.member_first_name}****MI*{claim_info.member_id}")
        segment_count += 1
        
        # HL*3 - Service Provider Level
        segments.append("HL*3*2*23*0")
        segment_count += 1
        
        # STC - Claim Status
        stc_composite = f"{claim_info.status_category_code}:{claim_info.status_code}:{claim_info.entity_identifier}"
        segments.append(f"STC*{stc_composite}*{gs_date}")
        segment_count += 1
        
        # REF*D9 - RFAI Reference
        if claim_info.rfai_reference:
            segments.append(f"REF*D9*{claim_info.rfai_reference}")
            segment_count += 1
        
        # REF*1K - Claim Number
        if claim_info.claim_number:
            segments.append(f"REF*1K*{claim_info.claim_number}")
            segment_count += 1
        
        # DTP*472 - Service Date (if provided)
        if claim_info.service_date:
            segments.append(f"DTP*472*D8*{claim_info.service_date}")
            segment_count += 1
        
        # SE - Transaction Set Trailer
        segments.append(f"SE*{segment_count + 1}*{st_control}")
        
        # GE - Functional Group Trailer
        segments.append(f"GE*1*{gs_control}")
        
        # IEA - Interchange Control Trailer
        segments.append(f"IEA*1*{isa_control}")
        
        # Increment counters
        self._isa_counter += 1
        self._gs_counter += 1
        self._st_counter += 1
        
        # Join with segment terminator
        edi_content = "~\n".join(segments) + "~\n"
        
        self.logger.info(f"Generated 277 with {segment_count} segments")
        return edi_content
    
    def _build_isa(self, envelope: X277Envelope, date: str, time: str, control_number: str) -> str:
        """Build ISA segment"""
        sender_id = self._pad_right(envelope.sender.identifier, 15)
        receiver_id = self._pad_right(envelope.receiver.identifier, 15)
        
        return (
            f"ISA*00*{' ' * 10}*00*{' ' * 10}*"
            f"{envelope.sender.qualifier}*{sender_id}*"
            f"{envelope.receiver.qualifier}*{receiver_id}*"
            f"{date}*{time}*^*00501*{control_number}*0*{envelope.usage_indicator}*:"
        )
    
    def _build_gs(self, envelope: X277Envelope, date: str, time: str, control_number: str) -> str:
        """Build GS segment"""
        return (
            f"GS*HI*{envelope.sender.identifier}*{envelope.receiver.identifier}*"
            f"{date}*{time}*{control_number}*X*{envelope.implementation_guide_version}"
        )
    
    def _pad_right(self, value: str, length: int) -> str:
        """Pad string to specified length with spaces"""
        return value.ljust(length)[:length]
    
    def _format_control_number(self, number: int, length: int) -> str:
        """Format control number with leading zeros"""
        return str(number).zfill(length)
    
    def from_json(self, json_data: Dict[str, Any]) -> Tuple[ClaimStatusInfo, X277Envelope]:
        """Parse JSON input to ClaimStatusInfo and X277Envelope"""
        claim_info = ClaimStatusInfo(
            claim_number=json_data.get("claimNumber", ""),
            member_id=json_data.get("memberId", ""),
            member_first_name=json_data.get("memberFirstName", ""),
            member_last_name=json_data.get("memberLastName", ""),
            provider_npi=json_data.get("providerNpi", ""),
            provider_name=json_data.get("providerName", ""),
            payer_id=json_data.get("payerId", ""),
            payer_name=json_data.get("payerName", ""),
            service_date=json_data.get("serviceDate", ""),
            rfai_reference=json_data.get("rfaiReference", ""),
            rfai_reason_code=json_data.get("rfaiReasonCode", "A1"),
            status_category_code=json_data.get("statusCategoryCode", "A1"),
            status_code=json_data.get("statusCode", "20"),
            entity_identifier=json_data.get("entityIdentifier", "41")
        )
        
        # Parse envelope configuration
        envelope_data = json_data.get("envelope", {})
        sender_data = envelope_data.get("sender", {})
        receiver_data = envelope_data.get("receiver", {})
        
        envelope = X277Envelope(
            sender=TradingPartner(
                qualifier=sender_data.get("qualifier", "ZZ"),
                identifier=sender_data.get("identifier", os.environ.get("SENDER_ID", "")),
                name=sender_data.get("name", "")
            ),
            receiver=TradingPartner(
                qualifier=receiver_data.get("qualifier", "ZZ"),
                identifier=receiver_data.get("identifier", os.environ.get("RECEIVER_ID", "")),
                name=receiver_data.get("name", "")
            ),
            interchange_control_number=envelope_data.get("interchangeControlNumber", ""),
            group_control_number=envelope_data.get("groupControlNumber", ""),
            transaction_set_control_number=envelope_data.get("transactionSetControlNumber", ""),
            implementation_guide_version=envelope_data.get("implementationGuideVersion", "005010X212"),
            usage_indicator=envelope_data.get("usageIndicator", "T")
        )
        
        return claim_info, envelope


def main():
    """Main entry point for X12 277 generator"""
    parser = argparse.ArgumentParser(
        description="Generate HIPAA X12 277 (Claim Status Notification / RFAI) EDI files"
    )
    
    # Input options (mutually exclusive: JSON file or individual fields)
    input_group = parser.add_mutually_exclusive_group(required=True)
    input_group.add_argument(
        "-i", "--input",
        help="Input JSON file with claim and envelope data"
    )
    input_group.add_argument(
        "--claim-number",
        help="Claim number for 277 generation"
    )
    
    # Individual field options (used with --claim-number)
    parser.add_argument("--member-id", help="Member ID")
    parser.add_argument("--member-first-name", help="Member first name")
    parser.add_argument("--member-last-name", help="Member last name")
    parser.add_argument("--provider-npi", help="Provider NPI")
    parser.add_argument("--payer-id", help="Payer ID")
    parser.add_argument("--payer-name", help="Payer name")
    parser.add_argument("--service-date", help="Service date (YYYYMMDD)")
    parser.add_argument("--rfai-reference", help="RFAI reference number")
    parser.add_argument("--rfai-reason", default="A1", help="RFAI reason code (default: A1)")
    parser.add_argument("--status-code", default="20", help="Status code (default: 20)")
    
    # Trading partner options
    parser.add_argument("--sender-id", default=os.environ.get("SENDER_ID", ""), help="Sender ID")
    parser.add_argument("--sender-qualifier", default="ZZ", help="Sender ID qualifier")
    parser.add_argument("--receiver-id", default=os.environ.get("RECEIVER_ID", ""), help="Receiver ID")
    parser.add_argument("--receiver-qualifier", default="ZZ", help="Receiver ID qualifier")
    
    # Output options
    parser.add_argument("-o", "--output", help="Output EDI file path (default: stdout)")
    parser.add_argument("-p", "--production", action="store_true", help="Use production indicator (P) instead of test (T)")
    
    # Logging
    parser.add_argument(
        "-l", "--log-level",
        default=os.environ.get("LOG_LEVEL", "INFO"),
        choices=["DEBUG", "INFO", "WARNING", "ERROR"],
        help="Logging level"
    )
    
    args = parser.parse_args()
    
    # Initialize generator
    generator = X12_277_Generator(log_level=args.log_level)
    
    # Build input data
    if args.input:
        # Load from JSON file
        with open(args.input, 'r') as f:
            json_data = json.load(f)
        claim_info, envelope = generator.from_json(json_data)
    else:
        # Build from command-line arguments
        claim_info = ClaimStatusInfo(
            claim_number=args.claim_number or "",
            member_id=args.member_id or "",
            member_first_name=args.member_first_name or "",
            member_last_name=args.member_last_name or "",
            provider_npi=args.provider_npi or "",
            payer_id=args.payer_id or args.sender_id,
            payer_name=args.payer_name or "",
            service_date=args.service_date or "",
            rfai_reference=args.rfai_reference or "",
            rfai_reason_code=args.rfai_reason,
            status_category_code="A1",
            status_code=args.status_code
        )
        
        envelope = X277Envelope(
            sender=TradingPartner(
                qualifier=args.sender_qualifier,
                identifier=args.sender_id
            ),
            receiver=TradingPartner(
                qualifier=args.receiver_qualifier,
                identifier=args.receiver_id
            ),
            usage_indicator="P" if args.production else "T"
        )
    
    # Generate EDI
    edi_content = generator.generate(claim_info, envelope)
    
    # Write output
    if args.output:
        with open(args.output, 'w') as f:
            f.write(edi_content)
        print(f"Output written to: {args.output}", file=sys.stderr)
    else:
        print(edi_content)
    
    sys.exit(0)


if __name__ == "__main__":
    main()
