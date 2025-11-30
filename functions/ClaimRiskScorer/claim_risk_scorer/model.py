"""
PyTorch-based fraud/abuse risk scoring model for healthcare claims.

This module provides the ClaimRiskModel class that loads a trained PyTorch model
and scores claims for fraud/abuse risk on a 0-100 scale.
"""

import logging
import os
from dataclasses import dataclass
from enum import Enum
from typing import List, Optional

logger = logging.getLogger(__name__)

# Risk reason codes
class RiskReasonCode(str, Enum):
    """Enumeration of possible risk reason codes."""
    HIGH_BILL_AMOUNT = "HIGH_BILL_AMOUNT"
    PROVIDER_HISTORY = "PROVIDER_HISTORY"
    PROCEDURE_MISMATCH = "PROCEDURE_MISMATCH"
    DUPLICATE_PATTERN = "DUPLICATE_PATTERN"
    UNBUNDLING = "UNBUNDLING"
    UPCODING = "UPCODING"
    OUT_OF_NETWORK = "OUT_OF_NETWORK"
    NEW_MEMBER = "NEW_MEMBER"
    UNUSUAL_FREQUENCY = "UNUSUAL_FREQUENCY"
    GEOGRAPHIC_ANOMALY = "GEOGRAPHIC_ANOMALY"


@dataclass
class RiskReason:
    """A reason contributing to the risk score."""
    code: str
    description: str
    contribution: float  # How much this reason contributes to the score (0-100)


@dataclass
class RiskResult:
    """Result of risk scoring a claim."""
    risk_score: float  # 0-100
    top_reasons: List[RiskReason]
    model_version: str
    features_used: List[str]


class ClaimRiskModel:
    """
    PyTorch-based claim fraud/abuse risk scoring model.
    
    This model loads a pre-trained PyTorch model from disk and uses it
    to score claims. If the model file is not available or fails to load,
    it falls back to a rule-based scoring system for development/testing.
    """
    
    # Feature names used by the model
    FEATURE_NAMES = [
        "bill_amount",
        "provider_risk_score",
        "member_tenure_days",
        "procedure_code_risk",
        "diagnosis_code_count",
        "modifier_count",
        "service_days",
        "out_of_network",
    ]
    
    # Default model path for Azure Functions runtime
    DEFAULT_MODEL_PATH = "./ml/claim-fraud-v1.pt"
    
    def __init__(self, model_path: str = None):
        """
        Initialize the risk scoring model.
        
        Args:
            model_path: Optional[str] = None. Path to the PyTorch model file. If None,
                uses DEFAULT_MODEL_PATH ("./ml/claim-fraud-v1.pt").
        """
        self.model_path = model_path or self.DEFAULT_MODEL_PATH
        self.model = None
        self.model_version = "rule-based-v1"
        self._use_pytorch = False
        
        self._load_model()
    
    def _load_model(self) -> None:
        """Attempt to load the PyTorch model from disk."""
        try:
            import torch
            
            if os.path.exists(self.model_path):
                # Check if it's a valid PyTorch file (not placeholder)
                file_size = os.path.getsize(self.model_path)
                
                if file_size > 1000:  # Real model would be larger than 1KB
                    self.model = torch.load(self.model_path, map_location="cpu", weights_only=True)
                    self._use_pytorch = True
                    self.model_version = "pytorch-v1"
                    logger.info(f"Loaded PyTorch model from {self.model_path}")
                else:
                    logger.info(
                        f"Model file at {self.model_path} appears to be a placeholder. "
                        "Using rule-based scoring."
                    )
            else:
                logger.info(
                    f"Model file not found at {self.model_path}. "
                    "Using rule-based scoring."
                )
                
        except ImportError:
            logger.warning("PyTorch not available. Using rule-based scoring.")
        except Exception as e:
            logger.warning(f"Failed to load PyTorch model: {e}. Using rule-based scoring.")
    
    def score_claim(self, claim: 'Claim837') -> RiskResult:
        """
        Score a claim for fraud/abuse risk.
        
        Args:
            claim: Parsed 837 claim data
            
        Returns:
            RiskResult with score (0-100) and top reasons
        """
        # Extract features from claim
        features = self._extract_features(claim)
        
        if self._use_pytorch and self.model is not None:
            return self._score_with_pytorch(features)
        else:
            return self._score_with_rules(features, claim)
    
    def _extract_features(self, claim: 'Claim837') -> dict:
        """Extract numerical features from a claim for model input."""
        return {
            "bill_amount": claim.bill_amount or 0.0,
            "provider_risk_score": claim.provider_risk_score or 0.0,
            "member_tenure_days": claim.member_tenure_days or 365,
            "procedure_code_risk": self._get_procedure_risk(claim.procedure_codes),
            "diagnosis_code_count": len(claim.diagnosis_codes) if claim.diagnosis_codes else 1,
            "modifier_count": len(claim.modifiers) if claim.modifiers else 0,
            "service_days": claim.service_days or 1,
            "out_of_network": 1.0 if claim.out_of_network else 0.0,
        }
    
    def _get_procedure_risk(self, procedure_codes: Optional[List[str]]) -> float:
        """
        Calculate aggregate procedure code risk.
        
        Some procedure codes are more commonly associated with fraud.
        """
        if not procedure_codes:
            return 0.0
        
        # High-risk procedure codes (example - in production, use actual risk database)
        high_risk_codes = {"99215", "99214", "99213", "99223", "99232"}
        
        risk_count = sum(1 for code in procedure_codes if code in high_risk_codes)
        return min(risk_count / len(procedure_codes), 1.0)
    
    def _score_with_pytorch(self, features: dict) -> RiskResult:
        """Score using the PyTorch model."""
        import torch
        
        # Convert features to tensor
        feature_values = [features[name] for name in self.FEATURE_NAMES]
        input_tensor = torch.tensor([feature_values], dtype=torch.float32)
        
        # Run inference
        with torch.no_grad():
            output = self.model(input_tensor)
            risk_score = float(output.squeeze().item())
        
        # Clamp to 0-100
        risk_score = max(0.0, min(100.0, risk_score))
        
        # Get reasons based on feature contributions
        reasons = self._compute_reasons_pytorch(features, input_tensor)
        
        return RiskResult(
            risk_score=risk_score,
            top_reasons=reasons,
            model_version=self.model_version,
            features_used=self.FEATURE_NAMES,
        )
    
    def _score_with_rules(self, features: dict, claim: 'Claim837') -> RiskResult:
        """
        Score using rule-based heuristics.
        
        This is used when PyTorch model is not available.
        """
        score = 0.0
        reasons = []
        
        # Rule 1: High bill amount
        bill_amount = features["bill_amount"]
        if bill_amount > 50000:
            contribution = min(30.0, (bill_amount - 50000) / 5000 * 5)
            score += contribution
            reasons.append(RiskReason(
                code=RiskReasonCode.HIGH_BILL_AMOUNT.value,
                description="Unusually high billed amount",
                contribution=contribution,
            ))
        elif bill_amount > 10000:
            contribution = min(15.0, (bill_amount - 10000) / 5000 * 5)
            score += contribution
            reasons.append(RiskReason(
                code=RiskReasonCode.HIGH_BILL_AMOUNT.value,
                description="Above-average billed amount",
                contribution=contribution,
            ))
        
        # Rule 2: Provider risk history
        provider_risk = features["provider_risk_score"]
        if provider_risk > 0.5:
            contribution = provider_risk * 25
            score += contribution
            reasons.append(RiskReason(
                code=RiskReasonCode.PROVIDER_HISTORY.value,
                description="Provider has historical fraud indicators",
                contribution=contribution,
            ))
        
        # Rule 3: New member with high claim
        tenure_days = features["member_tenure_days"]
        if tenure_days < 90 and bill_amount > 5000:
            contribution = 15.0
            score += contribution
            reasons.append(RiskReason(
                code=RiskReasonCode.NEW_MEMBER.value,
                description="New member with high-cost claim",
                contribution=contribution,
            ))
        
        # Rule 4: Out of network
        if features["out_of_network"] > 0:
            contribution = 10.0
            score += contribution
            reasons.append(RiskReason(
                code=RiskReasonCode.OUT_OF_NETWORK.value,
                description="Out-of-network provider",
                contribution=contribution,
            ))
        
        # Rule 5: High modifier count (potential unbundling)
        modifier_count = features["modifier_count"]
        if modifier_count > 3:
            contribution = min(15.0, modifier_count * 3)
            score += contribution
            reasons.append(RiskReason(
                code=RiskReasonCode.UNBUNDLING.value,
                description="Multiple modifiers may indicate unbundling",
                contribution=contribution,
            ))
        
        # Rule 6: Procedure code risk
        proc_risk = features["procedure_code_risk"]
        if proc_risk > 0.3:
            contribution = proc_risk * 20
            score += contribution
            reasons.append(RiskReason(
                code=RiskReasonCode.UPCODING.value,
                description="High-risk procedure codes detected",
                contribution=contribution,
            ))
        
        # Rule 7: Multiple diagnoses
        diag_count = features["diagnosis_code_count"]
        if diag_count > 5:
            contribution = min(10.0, (diag_count - 5) * 2)
            score += contribution
            reasons.append(RiskReason(
                code=RiskReasonCode.PROCEDURE_MISMATCH.value,
                description="Unusually high number of diagnosis codes",
                contribution=contribution,
            ))
        
        # Clamp score to 0-100
        score = max(0.0, min(100.0, score))
        
        # Sort reasons by contribution and take top 3
        reasons.sort(key=lambda r: r.contribution, reverse=True)
        top_reasons = reasons[:3] if reasons else [
            RiskReason(
                code="LOW_RISK",
                description="No significant risk factors identified",
                contribution=0.0,
            )
        ]
        
        return RiskResult(
            risk_score=round(score, 2),
            top_reasons=top_reasons,
            model_version=self.model_version,
            features_used=self.FEATURE_NAMES,
        )
    
    def _compute_reasons_pytorch(self, features: dict, input_tensor) -> List[RiskReason]:
        """
        Compute risk reasons from PyTorch model using feature importance.
        
        This uses a simple gradient-based attribution approach.
        """
        # For now, fall back to rule-based reason computation
        # In production, implement proper feature attribution (SHAP, LIME, etc.)
        reasons = []
        
        if features["bill_amount"] > 10000:
            reasons.append(RiskReason(
                code=RiskReasonCode.HIGH_BILL_AMOUNT.value,
                description="High billed amount",
                contribution=20.0,
            ))
        
        if features["provider_risk_score"] > 0.3:
            reasons.append(RiskReason(
                code=RiskReasonCode.PROVIDER_HISTORY.value,
                description="Provider risk indicators",
                contribution=15.0,
            ))
        
        if features["out_of_network"] > 0:
            reasons.append(RiskReason(
                code=RiskReasonCode.OUT_OF_NETWORK.value,
                description="Out-of-network claim",
                contribution=10.0,
            ))
        
        if not reasons:
            reasons.append(RiskReason(
                code="LOW_RISK",
                description="No significant risk factors",
                contribution=0.0,
            ))
        
        return reasons[:3]
