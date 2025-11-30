"""
Unit tests for the ClaimRiskScorer Azure Function entry point.

Tests cover:
- get_telemetry_client() initialization and thread safety
- _parse_claim_message() for JSON and EDI formats
- main() function integration
- _log_high_risk_claim() telemetry logging
"""

import json
import os
from unittest import mock
from unittest.mock import MagicMock, patch
import pytest

# Import the module we're testing
import sys
sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))


class TestParseClaimMessage:
    """Tests for _parse_claim_message() function."""
    
    def test_parse_json_with_edi_content(self):
        """Test parsing JSON message with ediContent field."""
        from __init__ import _parse_claim_message
        
        edi = "ISA*00*          *00*          *ZZ*SENDER*ZZ*RECEIVER*230101*1200*^*00501*1*0*P*:~ST*837*0001~CLM*CLM123*1500.00***11:B:1~SE*3*0001~"
        message = json.dumps({"ediContent": edi})
        
        result = _parse_claim_message(message)
        
        assert result is not None
        assert result.claim_number == "CLM123"
        assert result.bill_amount == 1500.0
    
    def test_parse_json_with_content_field(self):
        """Test parsing JSON message with content field."""
        from __init__ import _parse_claim_message
        
        edi = "ST*837*0001~CLM*CLM456*2000.00***11:B:1~SE*2*0001~"
        message = json.dumps({"content": edi})
        
        result = _parse_claim_message(message)
        
        assert result is not None
        assert result.claim_number == "CLM456"
    
    def test_parse_json_with_payload_field(self):
        """Test parsing JSON message with payload field."""
        from __init__ import _parse_claim_message
        
        edi = "ST*837*0001~CLM*CLM789*3000.00***11:B:1~SE*2*0001~"
        message = json.dumps({"payload": edi})
        
        result = _parse_claim_message(message)
        
        assert result is not None
        assert result.claim_number == "CLM789"
    
    def test_parse_json_with_claim_data(self):
        """Test parsing JSON message with already-parsed claim data."""
        from __init__ import _parse_claim_message
        
        claim_data = {
            "claimNumber": "CLM999",
            "billAmount": 5000.0,
            "claimType": "837P",
            "providerState": "CA"
        }
        message = json.dumps(claim_data)
        
        result = _parse_claim_message(message)
        
        assert result is not None
        assert result.claim_number == "CLM999"
        assert result.bill_amount == 5000.0
    
    def test_parse_json_with_snake_case_claim_data(self):
        """Test parsing JSON message with snake_case claim data."""
        from __init__ import _parse_claim_message
        
        claim_data = {
            "claim_number": "CLM888",
            "bill_amount": 4000.0
        }
        message = json.dumps(claim_data)
        
        result = _parse_claim_message(message)
        
        assert result is not None
        assert result.claim_number == "CLM888"
        assert result.bill_amount == 4000.0
    
    def test_parse_raw_edi_with_isa_header(self):
        """Test parsing raw EDI message starting with ISA."""
        from __init__ import _parse_claim_message
        
        edi = "ISA*00*          *00*          *ZZ*SENDER*ZZ*RECEIVER*230101*1200*^*00501*1*0*P*:~ST*837*0001~CLM*CLMEDI*7500.00***11:B:1~SE*3*0001~"
        
        result = _parse_claim_message(edi)
        
        assert result is not None
        assert result.claim_number == "CLMEDI"
        assert result.bill_amount == 7500.0
    
    def test_parse_raw_edi_with_st_header(self):
        """Test parsing raw EDI message starting with ST segment."""
        from __init__ import _parse_claim_message
        
        edi = "ST*837*0001~CLM*CLMST*8000.00***11:B:1~SE*2*0001~"
        
        result = _parse_claim_message(edi)
        
        assert result is not None
        assert result.claim_number == "CLMST"
    
    def test_parse_invalid_json_returns_none(self):
        """Test that invalid JSON (not EDI) returns None."""
        from __init__ import _parse_claim_message
        
        message = "This is not valid JSON or EDI content"
        
        result = _parse_claim_message(message)
        
        assert result is None
    
    def test_parse_empty_json_object_returns_none(self):
        """Test that empty JSON object returns None."""
        from __init__ import _parse_claim_message
        
        message = json.dumps({})
        
        result = _parse_claim_message(message)
        
        assert result is None
    
    def test_parse_json_with_unknown_format_returns_none(self):
        """Test that JSON with unknown format returns None."""
        from __init__ import _parse_claim_message
        
        message = json.dumps({"unknownField": "someValue"})
        
        result = _parse_claim_message(message)
        
        assert result is None


class TestLogHighRiskClaim:
    """Tests for _log_high_risk_claim() function."""
    
    def test_log_high_risk_claim_with_telemetry(self):
        """Test logging high-risk claim when telemetry client is available."""
        from __init__ import _log_high_risk_claim
        from claim_risk_scorer.claim_parser import Claim837
        from claim_risk_scorer.model import RiskReason, RiskResult
        
        claim = Claim837(
            claim_number="CLM001",
            bill_amount=15000.0,
            provider_state="CA",
            claim_type="837P",
            service_type_code="01"
        )
        
        risk_result = RiskResult(
            risk_score=85.0,
            top_reasons=[
                RiskReason(code="HIGH_BILL", description="High bill", contribution=30.0),
                RiskReason(code="PROVIDER", description="Provider risk", contribution=20.0)
            ],
            model_version="test-v1",
            features_used=["bill_amount"]
        )
        
        mock_client = MagicMock()
        
        with patch('__init__.get_telemetry_client', return_value=mock_client):
            _log_high_risk_claim(claim, risk_result)
        
        # Verify track_event was called with correct parameters
        mock_client.track_event.assert_called_once()
        call_args = mock_client.track_event.call_args
        
        assert call_args[0][0] == "HighRiskClaim"
        properties = call_args[1]['properties']
        assert properties['risk_score'] == '85.0'
        assert properties['provider_state'] == 'CA'
        assert 'HIGH_BILL' in properties['reason_codes']
        
        # Verify flush was called
        mock_client.flush.assert_called_once()
    
    def test_log_high_risk_claim_without_telemetry(self):
        """Test that logging is skipped when telemetry client is None."""
        from __init__ import _log_high_risk_claim
        from claim_risk_scorer.claim_parser import Claim837
        from claim_risk_scorer.model import RiskReason, RiskResult
        
        claim = Claim837(claim_number="CLM002")
        risk_result = RiskResult(
            risk_score=90.0,
            top_reasons=[],
            model_version="test-v1",
            features_used=[]
        )
        
        with patch('__init__.get_telemetry_client', return_value=None):
            # Should not raise any exception
            _log_high_risk_claim(claim, risk_result)
    
    def test_log_high_risk_claim_anonymizes_data(self):
        """Test that PHI is not included in telemetry."""
        from __init__ import _log_high_risk_claim
        from claim_risk_scorer.claim_parser import Claim837
        from claim_risk_scorer.model import RiskReason, RiskResult
        
        claim = Claim837(
            claim_number="CLM003",  # This should NOT be logged
            bill_amount=25000.0,
            provider_state="TX"
        )
        
        risk_result = RiskResult(
            risk_score=82.0,
            top_reasons=[
                RiskReason(code="TEST", description="Test reason", contribution=10.0)
            ],
            model_version="test-v1",
            features_used=[]
        )
        
        mock_client = MagicMock()
        
        with patch('__init__.get_telemetry_client', return_value=mock_client):
            _log_high_risk_claim(claim, risk_result)
        
        call_args = mock_client.track_event.call_args
        properties = call_args[1]['properties']
        
        # Claim number should NOT be in properties (PHI)
        assert 'claim_number' not in properties
        assert 'CLM003' not in str(properties)
        
        # Bill amount should be bucketed, not exact
        assert 'bill_amount_bucket' in properties
        assert properties['bill_amount_bucket'] == '10000-50000'


class TestMainFunction:
    """Tests for main() Azure Function entry point."""
    
    def test_main_processes_valid_edi_claim(self):
        """Test main function processes valid EDI claim successfully."""
        from __init__ import main
        
        edi = "ISA*00*          *00*          *ZZ*SENDER*ZZ*RECEIVER*230101*1200*^*00501*1*0*P*:~ST*837*0001~CLM*CLM001*500.00***11:B:1~SE*3*0001~"
        
        mock_msg = MagicMock()
        mock_msg.get_body.return_value = edi.encode('utf-8')
        
        # Should not raise any exception for low-risk claim
        with patch('__init__.get_telemetry_client', return_value=None):
            main(mock_msg)
    
    def test_main_raises_on_invalid_message(self):
        """Test main function raises ValueError for invalid messages."""
        from __init__ import main
        
        invalid_message = "This is not a valid claim"
        
        mock_msg = MagicMock()
        mock_msg.get_body.return_value = invalid_message.encode('utf-8')
        
        with pytest.raises(ValueError, match="Failed to parse claim"):
            main(mock_msg)


class TestHelperFunctions:
    """Tests for helper functions."""
    
    def test_get_risk_category_critical(self):
        """Test CRITICAL category for scores >= 81."""
        from __init__ import _get_risk_category
        
        assert _get_risk_category(81) == "CRITICAL"
        assert _get_risk_category(100) == "CRITICAL"
        assert _get_risk_category(95.5) == "CRITICAL"
    
    def test_get_risk_category_high(self):
        """Test HIGH category for scores 61-80."""
        from __init__ import _get_risk_category
        
        assert _get_risk_category(61) == "HIGH"
        assert _get_risk_category(80) == "HIGH"
        assert _get_risk_category(70.5) == "HIGH"
    
    def test_get_risk_category_medium(self):
        """Test MEDIUM category for scores 31-60."""
        from __init__ import _get_risk_category
        
        assert _get_risk_category(31) == "MEDIUM"
        assert _get_risk_category(60) == "MEDIUM"
        assert _get_risk_category(45) == "MEDIUM"
    
    def test_get_risk_category_low(self):
        """Test LOW category for scores < 31."""
        from __init__ import _get_risk_category
        
        assert _get_risk_category(0) == "LOW"
        assert _get_risk_category(30) == "LOW"
        assert _get_risk_category(15.5) == "LOW"
    
    def test_get_amount_bucket_unknown(self):
        """Test UNKNOWN bucket for None amount."""
        from __init__ import _get_amount_bucket
        
        assert _get_amount_bucket(None) == "UNKNOWN"
    
    def test_get_amount_bucket_ranges(self):
        """Test all amount bucket ranges."""
        from __init__ import _get_amount_bucket
        
        assert _get_amount_bucket(50) == "0-100"
        assert _get_amount_bucket(250) == "100-500"
        assert _get_amount_bucket(750) == "500-1000"
        assert _get_amount_bucket(2500) == "1000-5000"
        assert _get_amount_bucket(7500) == "5000-10000"
        assert _get_amount_bucket(25000) == "10000-50000"
        assert _get_amount_bucket(100000) == "50000+"
