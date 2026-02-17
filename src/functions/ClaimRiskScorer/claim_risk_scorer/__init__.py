"""
ClaimRiskScorer package for Azure Function.

This package provides:
- PyTorch-based fraud/abuse risk scoring model
- X12 837 claim parsing utilities
- ZZZ segment generation for 277 responses
"""

from claim_risk_scorer.model import ClaimRiskModel, RiskResult, RiskReason, RiskReasonCode
from claim_risk_scorer.claim_parser import parse_837_claim, Claim837
from claim_risk_scorer.zzz_segment import generate_zzz_segment

__all__ = [
    "ClaimRiskModel",
    "RiskResult",
    "RiskReason",
    "RiskReasonCode",
    "parse_837_claim",
    "Claim837",
    "generate_zzz_segment",
]
