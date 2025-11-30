"""
ZZZ Segment Generator for 277 Responses.

Generates custom ZZZ segments to include fraud/abuse risk scoring
information in X12 277 claim status responses.

The ZZZ segment is a non-standard segment used for proprietary data
exchange between trading partners. It follows X12 syntax conventions.
"""

import logging
from typing import List

from claim_risk_scorer.model import RiskReason

logger = logging.getLogger(__name__)


def generate_zzz_segment(risk_score: float, reasons: List[RiskReason]) -> str:
    """
    Generate a ZZZ custom segment for 277 response.
    
    The ZZZ segment contains:
    - ZZZ01: Segment qualifier ("RS" for Risk Score)
    - ZZZ02: Risk score (0-100)
    - ZZZ03: Risk category
    - ZZZ04: First reason code
    - ZZZ05: First reason description
    - ZZZ06: Second reason code
    - ZZZ07: Second reason description  
    - ZZZ08: Third reason code
    - ZZZ09: Third reason description
    
    Args:
        risk_score: Numeric risk score (0-100)
        reasons: List of RiskReason objects (top 3 will be used)
        
    Returns:
        X12-formatted ZZZ segment string
    """
    # Round score to 2 decimal places
    score = round(risk_score, 2)
    
    # Determine risk category
    category = _get_risk_category(score)
    
    # Ensure we have at least 3 reasons (pad with empty if needed)
    padded_reasons = list(reasons[:3])
    while len(padded_reasons) < 3:
        padded_reasons.append(RiskReason(code="", description="", contribution=0.0))
    
    # Build segment elements
    elements = [
        "ZZZ",                                    # Segment ID
        "RS",                                     # Qualifier: Risk Score
        str(score),                               # Risk score value
        category,                                 # Risk category
        _sanitize_element(padded_reasons[0].code),       # Reason 1 code
        _sanitize_element(padded_reasons[0].description), # Reason 1 description
        _sanitize_element(padded_reasons[1].code),       # Reason 2 code
        _sanitize_element(padded_reasons[1].description), # Reason 2 description
        _sanitize_element(padded_reasons[2].code),       # Reason 3 code
        _sanitize_element(padded_reasons[2].description), # Reason 3 description
    ]
    
    # Join with element separator and add segment terminator
    segment = "*".join(elements) + "~"
    
    logger.debug(f"Generated ZZZ segment: {segment}")
    return segment


def _get_risk_category(score: float) -> str:
    """Convert numeric score to risk category code."""
    if score >= 81:
        return "CR"  # Critical
    elif score >= 61:
        return "HI"  # High
    elif score >= 31:
        return "MD"  # Medium
    else:
        return "LO"  # Low


def _sanitize_element(value: str) -> str:
    """
    Sanitize a value for use in X12 segment.
    
    X12 elements cannot contain:
    - Element separator (*)
    - Segment terminator (~)
    - Sub-element separator (:)
    
    Also truncates to reasonable length for EDI.
    """
    if not value:
        return ""
    
    # Replace reserved characters
    sanitized = value.replace("*", " ").replace("~", " ").replace(":", " ")
    
    # Remove line breaks and extra whitespace
    sanitized = " ".join(sanitized.split())
    
    # Truncate to 80 characters (reasonable EDI limit)
    if len(sanitized) > 80:
        sanitized = sanitized[:77] + "..."
    
    return sanitized


def parse_zzz_segment(segment: str) -> dict:
    """
    Parse a ZZZ risk score segment back into structured data.
    
    Useful for testing and validation.
    
    Args:
        segment: X12 ZZZ segment string
        
    Returns:
        Dictionary with parsed values
    """
    # Remove segment terminator
    segment = segment.rstrip("~")
    
    elements = segment.split("*")
    
    if len(elements) < 4 or elements[0] != "ZZZ":
        raise ValueError(f"Invalid ZZZ segment: {segment}")
    
    result = {
        "qualifier": elements[1] if len(elements) > 1 else "",
        "risk_score": float(elements[2]) if len(elements) > 2 and elements[2] else 0.0,
        "risk_category": elements[3] if len(elements) > 3 else "",
        "reasons": []
    }
    
    # Parse up to 3 reasons (pairs of code + description)
    for i in range(3):
        code_idx = 4 + (i * 2)
        desc_idx = 5 + (i * 2)
        
        code = elements[code_idx] if len(elements) > code_idx else ""
        desc = elements[desc_idx] if len(elements) > desc_idx else ""
        
        if code:
            result["reasons"].append({
                "code": code,
                "description": desc
            })
    
    return result
