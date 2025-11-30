"""
X12 837 Claim Parser for ClaimRiskScorer.

Parses X12 837 (Professional and Institutional) claims to extract
features needed for fraud/abuse risk scoring.

HIPAA Compliance:
- Parser extracts only the fields needed for risk scoring
- PHI fields (member name, SSN, etc.) are NOT extracted
- Only aggregate/derived features are exposed to the model
"""

import logging
from dataclasses import dataclass, field
from datetime import datetime
from typing import List, Optional

logger = logging.getLogger(__name__)


@dataclass
class Claim837:
    """
    Parsed 837 claim data structure.
    
    Contains only the fields needed for risk scoring - no PHI.
    """
    # Claim identification (anonymized)
    claim_number: Optional[str] = None
    claim_type: str = "837P"  # 837P (Professional) or 837I (Institutional)
    
    # Billing amounts
    bill_amount: float = 0.0
    
    # Provider information (aggregated/anonymized)
    provider_npi: Optional[str] = None
    provider_state: Optional[str] = None
    provider_risk_score: float = 0.0  # From external provider database
    out_of_network: bool = False
    
    # Service information
    service_date: Optional[str] = None
    service_days: int = 1
    service_type_code: Optional[str] = None
    
    # Codes
    procedure_codes: List[str] = field(default_factory=list)
    diagnosis_codes: List[str] = field(default_factory=list)
    modifiers: List[str] = field(default_factory=list)
    
    # Member tenure (days enrolled - no PHI)
    member_tenure_days: int = 365
    
    @classmethod
    def from_dict(cls, data: dict) -> 'Claim837':
        """Create Claim837 from dictionary (e.g., from JSON message)."""
        # Handle both camelCase and snake_case keys
        return cls(
            claim_number=data.get("claimNumber") or data.get("claim_number"),
            claim_type=data.get("claimType") or data.get("claim_type", "837P"),
            bill_amount=float(data.get("billAmount") or data.get("bill_amount", 0)),
            provider_npi=data.get("providerNpi") or data.get("provider_npi"),
            provider_state=data.get("providerState") or data.get("provider_state"),
            provider_risk_score=float(data.get("providerRiskScore") or data.get("provider_risk_score", 0)),
            out_of_network=bool(data.get("outOfNetwork") or data.get("out_of_network", False)),
            service_date=data.get("serviceDate") or data.get("service_date"),
            service_days=int(data.get("serviceDays") or data.get("service_days", 1)),
            service_type_code=data.get("serviceTypeCode") or data.get("service_type_code"),
            procedure_codes=data.get("procedureCodes") or data.get("procedure_codes", []),
            diagnosis_codes=data.get("diagnosisCodes") or data.get("diagnosis_codes", []),
            modifiers=data.get("modifiers", []),
            member_tenure_days=int(data.get("memberTenureDays") or data.get("member_tenure_days", 365)),
        )


def parse_837_claim(edi_content: str) -> Optional[Claim837]:
    """
    Parse X12 837 EDI content to extract claim features.
    
    Args:
        edi_content: Raw X12 837 EDI string
        
    Returns:
        Parsed Claim837 object or None if parsing fails
    """
    try:
        # Validate input
        if not edi_content or not edi_content.strip():
            logger.warning("Empty EDI content provided")
            return None
        
        # Check for basic EDI structure markers
        if not any(marker in edi_content for marker in ["ISA*", "ST*", "CLM*"]):
            logger.warning("Content does not appear to be valid EDI")
            return None
        
        # Detect claim type from ST segment
        claim_type = "837P"  # Default to Professional
        if "ST*837*" in edi_content:
            # Check BHT or other indicators for Institutional
            if "0019*13" in edi_content or "CLM*" in edi_content and "*P~" in edi_content:
                claim_type = "837I"
        
        claim = Claim837(claim_type=claim_type)
        
        # Parse segments
        segments = _split_segments(edi_content)
        
        for segment in segments:
            _parse_segment(segment, claim)
        
        logger.debug(f"Parsed 837 claim: type={claim.claim_type}, amount={claim.bill_amount}")
        return claim
        
    except Exception as e:
        logger.error(f"Failed to parse 837 claim: {e}")
        return None


def _split_segments(edi_content: str) -> List[str]:
    """Split EDI content into segments."""
    # Handle different segment terminators
    if "~" in edi_content:
        segments = edi_content.split("~")
    elif "\n" in edi_content:
        segments = edi_content.split("\n")
    else:
        segments = [edi_content]
    
    return [s.strip() for s in segments if s.strip()]


def _parse_segment(segment: str, claim: Claim837) -> None:
    """Parse a single EDI segment and update claim."""
    elements = segment.split("*")
    
    if not elements:
        return
    
    segment_id = elements[0]
    
    # CLM - Claim Information
    if segment_id == "CLM":
        _parse_clm(elements, claim)
    
    # NM1 - Entity Names (for provider NPI)
    elif segment_id == "NM1":
        _parse_nm1(elements, claim)
    
    # SV1 - Professional Service
    elif segment_id == "SV1":
        _parse_sv1(elements, claim)
    
    # SV2 - Institutional Service
    elif segment_id == "SV2":
        _parse_sv2(elements, claim)
    
    # HI - Health Care Diagnosis Code
    elif segment_id == "HI":
        _parse_hi(elements, claim)
    
    # DTP - Date/Time
    elif segment_id == "DTP":
        _parse_dtp(elements, claim)
    
    # N4 - Geographic Location (for provider state)
    elif segment_id == "N4":
        _parse_n4(elements, claim)


def _parse_clm(elements: List[str], claim: Claim837) -> None:
    """Parse CLM (Claim Information) segment."""
    if len(elements) > 1:
        claim.claim_number = elements[1]
    
    if len(elements) > 2:
        try:
            claim.bill_amount = float(elements[2])
        except ValueError:
            logger.warning(
                f"Failed to parse bill_amount from CLM segment: '{elements[2]}'. Defaulting to 0.0."
            )


def _parse_nm1(elements: List[str], claim: Claim837) -> None:
    """Parse NM1 (Entity Name) segment."""
    if len(elements) < 2:
        return
    
    entity_code = elements[1]
    
    # Billing/Rendering Provider
    if entity_code in ("85", "82"):
        # NM109 = Provider NPI (if qualifier is XX)
        if len(elements) > 9 and elements[8] == "XX":
            claim.provider_npi = elements[9]


def _parse_sv1(elements: List[str], claim: Claim837) -> None:
    """Parse SV1 (Professional Service) segment."""
    if len(elements) < 2:
        return
    
    # SV101 contains procedure code (composite element)
    composite = elements[1].split(":")
    if composite:
        # First component is code qualifier, second is code
        if len(composite) > 1:
            claim.procedure_codes.append(composite[1])
        else:
            claim.procedure_codes.append(composite[0])
    
    # Extract modifiers from composite (positions 3-6)
    for i in range(2, min(6, len(composite))):
        if composite[i]:
            claim.modifiers.append(composite[i])


def _parse_sv2(elements: List[str], claim: Claim837) -> None:
    """Parse SV2 (Institutional Service) segment."""
    if len(elements) < 2:
        return
    
    # SV202 contains procedure code composite
    if len(elements) > 2:
        composite = elements[2].split(":")
        if len(composite) > 1:
            claim.procedure_codes.append(composite[1])


def _parse_hi(elements: List[str], claim: Claim837) -> None:
    """Parse HI (Health Care Diagnosis Code) segment."""
    # HI segment contains diagnosis codes in composite elements
    for i in range(1, len(elements)):
        composite = elements[i].split(":")
        if len(composite) >= 2:
            # composite[0] is qualifier (ABK, ABF, etc.)
            # composite[1] is the diagnosis code
            claim.diagnosis_codes.append(composite[1])


def _parse_dtp(elements: List[str], claim: Claim837) -> None:
    """Parse DTP (Date/Time) segment."""
    if len(elements) < 4:
        return
    
    qualifier = elements[1]
    
    # 472 = Service Date
    if qualifier == "472":
        claim.service_date = elements[3]
        
        # If date range (RD8 format), calculate service days
        if elements[2] == "RD8" and "-" in elements[3]:
            try:
                start, end = elements[3].split("-")
                # Parse dates using proper datetime parsing (YYYYMMDD format)
                start_date = datetime.strptime(start, "%Y%m%d")
                end_date = datetime.strptime(end, "%Y%m%d")
                claim.service_days = max(1, (end_date - start_date).days + 1)
            except ValueError as e:
                logger.warning(f"Failed to parse service date range '{elements[3]}' in DTP segment: {e}")


def _parse_n4(elements: List[str], claim: Claim837) -> None:
    """Parse N4 (Geographic Location) segment."""
    # N402 = State
    if len(elements) > 2:
        claim.provider_state = elements[2]
