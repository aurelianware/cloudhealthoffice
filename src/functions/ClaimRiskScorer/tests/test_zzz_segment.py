"""
Unit tests for zzz_segment module.
"""

import pytest
from claim_risk_scorer.model import RiskReason
from claim_risk_scorer.zzz_segment import generate_zzz_segment, parse_zzz_segment


class TestGenerateZzzSegment:
    """Tests for generate_zzz_segment function."""
    
    def test_generate_basic_segment(self):
        """Test generating a basic ZZZ segment."""
        reasons = [
            RiskReason(code="HIGH_BILL_AMOUNT", description="High billed amount", contribution=25.0),
            RiskReason(code="PROVIDER_HISTORY", description="Provider risk", contribution=15.0),
            RiskReason(code="OUT_OF_NETWORK", description="Out of network", contribution=10.0),
        ]
        
        segment = generate_zzz_segment(risk_score=75.5, reasons=reasons)
        
        assert segment.startswith("ZZZ*")
        assert segment.endswith("~")
        assert "RS*" in segment  # Risk Score qualifier
        assert "75.5*" in segment
        assert "HIGH_BILL_AMOUNT*" in segment
        assert "PROVIDER_HISTORY*" in segment
        assert "OUT_OF_NETWORK*" in segment
    
    def test_generate_segment_categories(self):
        """Test that risk categories are correct."""
        reasons = [RiskReason(code="TEST", description="Test", contribution=10.0)]
        
        # Low risk
        segment_lo = generate_zzz_segment(risk_score=20.0, reasons=reasons)
        assert "*LO*" in segment_lo
        
        # Medium risk
        segment_md = generate_zzz_segment(risk_score=45.0, reasons=reasons)
        assert "*MD*" in segment_md
        
        # High risk
        segment_hi = generate_zzz_segment(risk_score=70.0, reasons=reasons)
        assert "*HI*" in segment_hi
        
        # Critical risk
        segment_cr = generate_zzz_segment(risk_score=90.0, reasons=reasons)
        assert "*CR*" in segment_cr
    
    def test_generate_segment_pads_reasons(self):
        """Test that segment pads to 3 reasons if fewer provided."""
        reasons = [
            RiskReason(code="ONLY_ONE", description="Only reason", contribution=50.0),
        ]
        
        segment = generate_zzz_segment(risk_score=60.0, reasons=reasons)
        
        # Should have all element separators for 3 reasons
        parts = segment.rstrip("~").split("*")
        # ZZZ, qualifier, score, category, + 6 elements for 3 reasons (code+desc each)
        assert len(parts) >= 10
    
    def test_generate_segment_truncates_reasons(self):
        """Test that segment uses only top 3 reasons."""
        reasons = [
            RiskReason(code="ONE", description="First", contribution=50.0),
            RiskReason(code="TWO", description="Second", contribution=40.0),
            RiskReason(code="THREE", description="Third", contribution=30.0),
            RiskReason(code="FOUR", description="Fourth - should not appear", contribution=20.0),
            RiskReason(code="FIVE", description="Fifth - should not appear", contribution=10.0),
        ]
        
        segment = generate_zzz_segment(risk_score=80.0, reasons=reasons)
        
        assert "ONE*" in segment
        assert "TWO*" in segment
        assert "THREE*" in segment
        assert "FOUR" not in segment
        assert "FIVE" not in segment
    
    def test_generate_segment_sanitizes_special_chars(self):
        """Test that special characters are sanitized."""
        reasons = [
            RiskReason(
                code="TEST*CODE",  # Contains element separator
                description="Description~with~terminators",  # Contains segment terminators
                contribution=50.0,
            ),
        ]
        
        segment = generate_zzz_segment(risk_score=50.0, reasons=reasons)
        
        # Should not have unescaped special characters (except proper separators)
        inner = segment[4:-1]  # Remove ZZZ* prefix and ~ suffix
        assert inner.count("~") == 0  # No extra terminators
    
    def test_generate_segment_truncates_long_descriptions(self):
        """Test that long descriptions are truncated."""
        long_desc = "A" * 200  # Very long description
        reasons = [
            RiskReason(code="TEST", description=long_desc, contribution=50.0),
        ]
        
        segment = generate_zzz_segment(risk_score=50.0, reasons=reasons)
        
        # Description should be truncated to 80 chars (with ellipsis)
        assert long_desc not in segment
        assert "..." in segment or len(segment) < len(long_desc) + 50
    
    def test_generate_segment_empty_reasons(self):
        """Test generating segment with empty reasons list."""
        segment = generate_zzz_segment(risk_score=10.0, reasons=[])
        
        assert segment.startswith("ZZZ*RS*10.0*LO*")
        assert segment.endswith("~")
    
    def test_generate_segment_rounds_score(self):
        """Test that score is rounded to 2 decimal places."""
        reasons = [RiskReason(code="TEST", description="Test", contribution=10.0)]
        
        segment = generate_zzz_segment(risk_score=75.555555, reasons=reasons)
        
        assert "75.56*" in segment  # Rounded


class TestParseZzzSegment:
    """Tests for parse_zzz_segment function."""
    
    def test_parse_basic_segment(self):
        """Test parsing a basic ZZZ segment."""
        segment = "ZZZ*RS*75.5*HI*HIGH_BILL*High amount*PROVIDER*Risk*NET*Network~"
        
        result = parse_zzz_segment(segment)
        
        assert result["qualifier"] == "RS"
        assert result["risk_score"] == 75.5
        assert result["risk_category"] == "HI"
        assert len(result["reasons"]) == 3
        assert result["reasons"][0]["code"] == "HIGH_BILL"
        assert result["reasons"][0]["description"] == "High amount"
    
    def test_parse_segment_without_terminator(self):
        """Test parsing segment without trailing terminator."""
        segment = "ZZZ*RS*50.0*MD*TEST*Description"
        
        result = parse_zzz_segment(segment)
        
        assert result["risk_score"] == 50.0
        assert result["risk_category"] == "MD"
    
    def test_parse_invalid_segment(self):
        """Test parsing invalid segment raises error."""
        with pytest.raises(ValueError):
            parse_zzz_segment("INVALID*SEGMENT")
    
    def test_parse_empty_reasons(self):
        """Test parsing segment with empty reasons."""
        segment = "ZZZ*RS*10.0*LO***~"
        
        result = parse_zzz_segment(segment)
        
        assert result["risk_score"] == 10.0
        assert len(result["reasons"]) == 0


class TestRoundTrip:
    """Test generating and parsing ZZZ segments."""
    
    def test_round_trip(self):
        """Test that generated segments can be parsed back."""
        reasons = [
            RiskReason(code="HIGH_BILL_AMOUNT", description="High billed amount", contribution=25.0),
            RiskReason(code="PROVIDER_HISTORY", description="Provider risk indicators", contribution=15.0),
            RiskReason(code="OUT_OF_NETWORK", description="Out of network provider", contribution=10.0),
        ]
        
        segment = generate_zzz_segment(risk_score=65.0, reasons=reasons)
        parsed = parse_zzz_segment(segment)
        
        assert parsed["qualifier"] == "RS"
        assert parsed["risk_score"] == 65.0
        assert parsed["risk_category"] == "HI"
        assert len(parsed["reasons"]) == 3
        assert parsed["reasons"][0]["code"] == "HIGH_BILL_AMOUNT"
        assert parsed["reasons"][1]["code"] == "PROVIDER_HISTORY"
        assert parsed["reasons"][2]["code"] == "OUT_OF_NETWORK"
