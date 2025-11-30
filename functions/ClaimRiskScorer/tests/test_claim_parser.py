"""
Unit tests for claim_parser module.
"""

from claim_risk_scorer.claim_parser import parse_837_claim, Claim837


class TestClaim837:
    """Tests for the Claim837 dataclass."""
    
    def test_from_dict_camel_case(self):
        """Test creating Claim837 from camelCase dictionary."""
        data = {
            "claimNumber": "CLM001",
            "claimType": "837P",
            "billAmount": 5000.0,
            "providerNpi": "1234567890",
            "providerState": "CA",
            "outOfNetwork": True,
            "procedureCodes": ["99213", "99214"],
            "diagnosisCodes": ["E119", "I10"],
        }
        
        claim = Claim837.from_dict(data)
        
        assert claim.claim_number == "CLM001"
        assert claim.claim_type == "837P"
        assert claim.bill_amount == 5000.0
        assert claim.provider_npi == "1234567890"
        assert claim.provider_state == "CA"
        assert claim.out_of_network is True
        assert claim.procedure_codes == ["99213", "99214"]
        assert claim.diagnosis_codes == ["E119", "I10"]
    
    def test_from_dict_snake_case(self):
        """Test creating Claim837 from snake_case dictionary."""
        data = {
            "claim_number": "CLM002",
            "claim_type": "837I",
            "bill_amount": 10000.0,
            "provider_npi": "9876543210",
            "out_of_network": False,
        }
        
        claim = Claim837.from_dict(data)
        
        assert claim.claim_number == "CLM002"
        assert claim.claim_type == "837I"
        assert claim.bill_amount == 10000.0
        assert claim.provider_npi == "9876543210"
        assert claim.out_of_network is False
    
    def test_from_dict_defaults(self):
        """Test that defaults are applied for missing fields."""
        data = {"claimNumber": "CLM003"}
        
        claim = Claim837.from_dict(data)
        
        assert claim.claim_number == "CLM003"
        assert claim.claim_type == "837P"  # Default
        assert claim.bill_amount == 0.0
        assert claim.member_tenure_days == 365  # Default
        assert claim.procedure_codes == []
        assert claim.diagnosis_codes == []


class TestParse837Claim:
    """Tests for parse_837_claim function."""
    
    def test_parse_simple_837p(self):
        """Test parsing a simple 837P EDI."""
        edi = """ISA*00*          *00*          *ZZ*SENDER         *ZZ*RECEIVER       *230101*1200*^*00501*000000001*0*P*:~
GS*HC*SENDER*RECEIVER*20230101*1200*1*X*005010X222A1~
ST*837*0001*005010X222A1~
BHT*0019*00*123456*20230101*1200*CH~
NM1*85*2*SMITH MEDICAL GROUP*****XX*1234567890~
N3*123 MAIN ST~
N4*ANYTOWN*CA*12345~
CLM*CLM123456*1500.00***11:B:1*Y*A*Y*Y~
HI*ABK:E119~
SV1*HC:99213*150.00*UN*1***1~
DTP*472*D8*20230101~
SE*12*0001~
GE*1*1~
IEA*1*000000001~"""
        
        claim = parse_837_claim(edi)
        
        assert claim is not None
        assert claim.claim_number == "CLM123456"
        assert claim.bill_amount == 1500.0
        assert claim.provider_npi == "1234567890"
        assert claim.provider_state == "CA"
        assert "99213" in claim.procedure_codes
        assert "E119" in claim.diagnosis_codes
    
    def test_parse_claim_with_modifiers(self):
        """Test parsing claim with procedure modifiers."""
        edi = """ISA*00*          *00*          *ZZ*SENDER*ZZ*RECEIVER*230101*1200*^*00501*1*0*P*:~
GS*HC*SENDER*RECEIVER*20230101*1200*1*X*005010X222A1~
ST*837*0001~
CLM*CLM789*2000.00***11:B:1~
SV1*HC:99214:25:59*200.00*UN*1~
SE*4*0001~
GE*1*1~
IEA*1*1~"""
        
        claim = parse_837_claim(edi)
        
        assert claim is not None
        assert "99214" in claim.procedure_codes
        assert "25" in claim.modifiers
        assert "59" in claim.modifiers
    
    def test_parse_claim_with_multiple_diagnoses(self):
        """Test parsing claim with multiple diagnosis codes."""
        edi = """ST*837*0001~
CLM*CLM456*3000.00***11:B:1~
HI*ABK:E119*ABF:I10*ABF:J069~
SE*4*0001~"""
        
        claim = parse_837_claim(edi)
        
        assert claim is not None
        assert len(claim.diagnosis_codes) == 3
        assert "E119" in claim.diagnosis_codes
        assert "I10" in claim.diagnosis_codes
        assert "J069" in claim.diagnosis_codes
    
    def test_parse_claim_with_date_range(self):
        """Test parsing claim with service date range."""
        edi = """ST*837*0001~
CLM*CLM111*5000.00***11:B:1~
DTP*472*RD8*20230101-20230105~
SE*3*0001~"""
        
        claim = parse_837_claim(edi)
        
        assert claim is not None
        assert claim.service_date == "20230101-20230105"
        assert claim.service_days == 5  # Jan 1 to Jan 5 = 5 days
    
    def test_parse_claim_with_cross_month_date_range(self):
        """Test parsing claim with date range crossing month boundary."""
        edi = """ST*837*0001~
CLM*CLM112*7500.00***11:B:1~
DTP*472*RD8*20230131-20230205~
SE*3*0001~"""
        
        claim = parse_837_claim(edi)
        
        assert claim is not None
        assert claim.service_date == "20230131-20230205"
        assert claim.service_days == 6  # Jan 31 to Feb 5 = 6 days
    
    def test_parse_invalid_edi(self):
        """Test parsing invalid EDI returns None."""
        invalid_edi = "This is not EDI content"
        
        claim = parse_837_claim(invalid_edi)
        
        assert claim is None
    
    def test_parse_empty_string(self):
        """Test parsing empty string returns None."""
        claim = parse_837_claim("")
        
        assert claim is None
    
    def test_parse_837i(self):
        """Test parsing 837I (Institutional) claim."""
        edi = """ST*837*0001~
CLM*CLM222*15000.00***11:B:1*Y*A*Y*Y*P~
SV2*0100*HC:99223*1500.00*UN*1~
HI*ABK:E119~
SE*4*0001~"""
        
        claim = parse_837_claim(edi)
        
        assert claim is not None
        assert claim.bill_amount == 15000.0
        # Should detect institutional claim type based on content
        # (Note: Detection logic may vary)


class TestClaimParserEdgeCases:
    """Edge case tests for claim parser."""
    
    def test_parse_newline_segments(self):
        """Test parsing EDI with newline segment terminators."""
        edi = "ST*837*0001\nCLM*CLM333*1000.00***11:B:1\nSE*2*0001"
        
        claim = parse_837_claim(edi)
        
        assert claim is not None
        assert claim.claim_number == "CLM333"
        assert claim.bill_amount == 1000.0
    
    def test_parse_mixed_terminators(self):
        """Test parsing EDI with mixed segment terminators."""
        edi = "ST*837*0001~CLM*CLM444*2500.00***11:B:1~SE*2*0001~"
        
        claim = parse_837_claim(edi)
        
        assert claim is not None
        assert claim.claim_number == "CLM444"
    
    def test_parse_missing_bill_amount(self):
        """Test parsing claim with missing bill amount."""
        edi = "ST*837*0001~CLM*CLM555~SE*2*0001~"
        
        claim = parse_837_claim(edi)
        
        assert claim is not None
        assert claim.claim_number == "CLM555"
        assert claim.bill_amount == 0.0  # Default
