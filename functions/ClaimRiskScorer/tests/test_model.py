"""
Unit tests for ClaimRiskModel.
"""

from claim_risk_scorer.model import ClaimRiskModel, RiskResult, RiskReasonCode
from claim_risk_scorer.claim_parser import Claim837


class TestClaimRiskModel:
    """Tests for the ClaimRiskModel class."""
    
    def setup_method(self):
        """Set up test fixtures."""
        # Model with non-existent path will use rule-based scoring
        self.model = ClaimRiskModel(model_path="./nonexistent/model.pt")
    
    def test_model_initialization(self):
        """Test model initializes correctly."""
        assert self.model is not None
        assert self.model.model_version == "rule-based-v1"
        assert not self.model._use_pytorch
    
    def test_score_low_risk_claim(self):
        """Test scoring a low-risk claim."""
        claim = Claim837(
            claim_number="CLM001",
            bill_amount=500.0,
            procedure_codes=["99211"],
            diagnosis_codes=["E119"],
            member_tenure_days=365,
            out_of_network=False,
        )
        
        result = self.model.score_claim(claim)
        
        assert isinstance(result, RiskResult)
        assert 0 <= result.risk_score <= 100
        assert result.risk_score < 30  # Low risk
        assert len(result.top_reasons) > 0
    
    def test_score_high_bill_amount(self):
        """Test that high bill amounts increase risk score."""
        claim = Claim837(
            claim_number="CLM002",
            bill_amount=75000.0,  # Very high
            procedure_codes=["99215"],
            diagnosis_codes=["E119"],
            member_tenure_days=365,
        )
        
        result = self.model.score_claim(claim)
        
        assert result.risk_score > 20  # Should be elevated
        assert any(r.code == RiskReasonCode.HIGH_BILL_AMOUNT.value 
                   for r in result.top_reasons)
    
    def test_score_out_of_network(self):
        """Test that out-of-network claims increase risk score."""
        claim = Claim837(
            claim_number="CLM003",
            bill_amount=5000.0,
            procedure_codes=["99213"],
            diagnosis_codes=["I10"],
            out_of_network=True,
        )
        
        result = self.model.score_claim(claim)
        
        assert any(r.code == RiskReasonCode.OUT_OF_NETWORK.value 
                   for r in result.top_reasons)
    
    def test_score_new_member_high_claim(self):
        """Test that new members with high claims are flagged."""
        claim = Claim837(
            claim_number="CLM004",
            bill_amount=10000.0,
            procedure_codes=["99215"],
            diagnosis_codes=["E119"],
            member_tenure_days=30,  # New member
        )
        
        result = self.model.score_claim(claim)
        
        assert any(r.code == RiskReasonCode.NEW_MEMBER.value 
                   for r in result.top_reasons)
    
    def test_score_multiple_modifiers(self):
        """Test that multiple modifiers increase risk (potential unbundling)."""
        claim = Claim837(
            claim_number="CLM005",
            bill_amount=3000.0,
            procedure_codes=["99214"],
            diagnosis_codes=["I10"],
            modifiers=["25", "59", "76", "77"],  # Many modifiers
        )
        
        result = self.model.score_claim(claim)
        
        assert any(r.code == RiskReasonCode.UNBUNDLING.value 
                   for r in result.top_reasons)
    
    def test_score_provider_history(self):
        """Test that provider risk history is considered."""
        claim = Claim837(
            claim_number="CLM006",
            bill_amount=2000.0,
            procedure_codes=["99213"],
            diagnosis_codes=["I10"],
            provider_risk_score=0.8,  # High provider risk
        )
        
        result = self.model.score_claim(claim)
        
        assert any(r.code == RiskReasonCode.PROVIDER_HISTORY.value 
                   for r in result.top_reasons)
    
    def test_score_result_structure(self):
        """Test that RiskResult has correct structure."""
        claim = Claim837(
            claim_number="CLM007",
            bill_amount=1000.0,
        )
        
        result = self.model.score_claim(claim)
        
        assert hasattr(result, "risk_score")
        assert hasattr(result, "top_reasons")
        assert hasattr(result, "model_version")
        assert hasattr(result, "features_used")
        assert len(result.features_used) == len(ClaimRiskModel.FEATURE_NAMES)
    
    def test_score_is_bounded(self):
        """Test that risk score is always between 0 and 100."""
        # Extreme high-risk claim
        high_risk_claim = Claim837(
            claim_number="CLM008",
            bill_amount=500000.0,
            procedure_codes=["99215", "99214", "99213"],
            diagnosis_codes=["E119", "I10", "J069", "M549", "R51", "K219"],
            modifiers=["25", "59", "76", "77", "50"],
            member_tenure_days=10,
            out_of_network=True,
            provider_risk_score=1.0,
        )
        
        result = self.model.score_claim(high_risk_claim)
        
        assert 0 <= result.risk_score <= 100
    
    def test_top_reasons_limited_to_three(self):
        """Test that only top 3 reasons are returned."""
        # Claim with many risk factors
        claim = Claim837(
            claim_number="CLM009",
            bill_amount=100000.0,
            procedure_codes=["99215"],
            modifiers=["25", "59", "76", "77"],
            member_tenure_days=10,
            out_of_network=True,
            provider_risk_score=0.9,
        )
        
        result = self.model.score_claim(claim)
        
        # Should have at most 3 reasons in top_reasons
        assert len(result.top_reasons) <= 3


class TestClaimRiskModelFeatures:
    """Tests for feature extraction."""
    
    def setup_method(self):
        """Set up test fixtures."""
        self.model = ClaimRiskModel(model_path="./nonexistent/model.pt")
    
    def test_extract_features_complete_claim(self):
        """Test feature extraction from a complete claim."""
        claim = Claim837(
            claim_number="CLM010",
            bill_amount=5000.0,
            provider_risk_score=0.3,
            member_tenure_days=180,
            procedure_codes=["99214", "99213"],
            diagnosis_codes=["E119", "I10"],
            modifiers=["25"],
            service_days=2,
            out_of_network=True,
        )
        
        features = self.model._extract_features(claim)
        
        assert features["bill_amount"] == 5000.0
        assert features["provider_risk_score"] == 0.3
        assert features["member_tenure_days"] == 180
        assert features["service_days"] == 2
        assert features["out_of_network"] == 1.0
        assert features["modifier_count"] == 1
        assert features["diagnosis_code_count"] == 2
    
    def test_extract_features_minimal_claim(self):
        """Test feature extraction from a minimal claim."""
        claim = Claim837(claim_number="CLM011")
        
        features = self.model._extract_features(claim)
        
        assert features["bill_amount"] == 0.0
        assert features["out_of_network"] == 0.0
        assert features["member_tenure_days"] == 365  # Default
