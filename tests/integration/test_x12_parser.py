#!/usr/bin/env python3
"""
Integration Tests for X12 Parser Container

Tests the X12 parser against sample 275, 277, and 278 EDI files.
"""

import json
import os
import sys
import unittest
from pathlib import Path

# Get the path to containers/x12-parser relative to this test file
CONTAINERS_PATH = Path(__file__).resolve().parent.parent.parent / 'containers' / 'x12-parser'
if CONTAINERS_PATH.exists():
    sys.path.insert(0, str(CONTAINERS_PATH))
else:
    # Fallback for running from different locations
    sys.path.insert(0, str(Path(__file__).resolve().parent.parent.parent / 'containers' / 'x12-parser'))

from parse_x12 import X12Parser, TransactionType


class TestX12Parser(unittest.TestCase):
    """Test cases for X12 parser"""
    
    @classmethod
    def setUpClass(cls):
        """Set up test fixtures"""
        cls.fixtures_dir = Path(__file__).parent.parent / 'fixtures'
        cls.parser = X12Parser(log_level="ERROR")
    
    def test_parse_275_attachment(self):
        """Test parsing 275 attachment request"""
        edi_file = self.fixtures_dir / 'test-x12-275.edi'
        
        if not edi_file.exists():
            self.skipTest(f"Test fixture not found: {edi_file}")
        
        result = self.parser.parse_file(str(edi_file))
        
        # Verify no parse errors
        self.assertEqual(len(result.parse_errors), 0, f"Parse errors: {result.parse_errors}")
        
        # Verify transaction type
        self.assertEqual(result.transaction_type, "275")
        
        # Verify ISA envelope
        self.assertIsNotNone(result.isa_envelope)
        self.assertEqual(result.isa_envelope.sender_id.strip(), "030240928")
        
        # Verify metadata extraction
        self.assertIn("member_id", result.metadata)
        self.assertIn("trace_number", result.metadata)
    
    def test_parse_277_status_notification(self):
        """Test parsing 277 claim status notification"""
        edi_file = self.fixtures_dir / 'test-x12-277.edi'
        
        if not edi_file.exists():
            self.skipTest(f"Test fixture not found: {edi_file}")
        
        result = self.parser.parse_file(str(edi_file))
        
        # Verify no parse errors
        self.assertEqual(len(result.parse_errors), 0, f"Parse errors: {result.parse_errors}")
        
        # Verify transaction type
        self.assertEqual(result.transaction_type, "277")
        
        # Verify ISA envelope
        self.assertIsNotNone(result.isa_envelope)
        
        # Verify 277-specific metadata
        self.assertIn("status_category_code", result.metadata)
    
    def test_parse_278_authorization(self):
        """Test parsing 278 health care services review"""
        edi_file = self.fixtures_dir / 'test-x12-278.edi'
        
        if not edi_file.exists():
            self.skipTest(f"Test fixture not found: {edi_file}")
        
        result = self.parser.parse_file(str(edi_file))
        
        # Verify no parse errors
        self.assertEqual(len(result.parse_errors), 0, f"Parse errors: {result.parse_errors}")
        
        # Verify transaction type
        self.assertEqual(result.transaction_type, "278")
        
        # Verify ISA envelope
        self.assertIsNotNone(result.isa_envelope)
        
        # Verify 278-specific metadata
        self.assertIn("review_action_code", result.metadata)
    
    def test_parse_invalid_content(self):
        """Test parsing invalid EDI content"""
        result = self.parser.parse_content("INVALID CONTENT")
        
        # Should have parse errors
        self.assertTrue(len(result.parse_errors) > 0)
    
    def test_json_output(self):
        """Test JSON serialization of parsed result"""
        edi_file = self.fixtures_dir / 'test-x12-275.edi'
        
        if not edi_file.exists():
            self.skipTest(f"Test fixture not found: {edi_file}")
        
        result = self.parser.parse_file(str(edi_file))
        json_output = self.parser.to_json(result)
        
        # Verify valid JSON
        parsed_json = json.loads(json_output)
        
        self.assertIn("transaction_type", parsed_json)
        self.assertIn("isa_envelope", parsed_json)
        self.assertIn("metadata", parsed_json)
    
    def test_isa_envelope_parsing(self):
        """Test ISA envelope field extraction"""
        edi_file = self.fixtures_dir / 'test-x12-275.edi'
        
        if not edi_file.exists():
            self.skipTest(f"Test fixture not found: {edi_file}")
        
        result = self.parser.parse_file(str(edi_file))
        isa = result.isa_envelope
        
        # Verify ISA fields
        self.assertIsNotNone(isa)
        self.assertEqual(isa.sender_id_qualifier, "ZZ")
        self.assertIsNotNone(isa.interchange_control_number)
        self.assertIn(isa.usage_indicator, ["T", "P"])  # Test or Production
    
    def test_gs_envelope_parsing(self):
        """Test GS envelope field extraction"""
        edi_file = self.fixtures_dir / 'test-x12-275.edi'
        
        if not edi_file.exists():
            self.skipTest(f"Test fixture not found: {edi_file}")
        
        result = self.parser.parse_file(str(edi_file))
        gs = result.gs_envelope
        
        # Verify GS fields
        self.assertIsNotNone(gs)
        self.assertEqual(gs.functional_id_code, "HI")  # Health Care Information
        self.assertIn("005010", gs.version_release_code)


class TestX12ParserMetadataExtraction(unittest.TestCase):
    """Test metadata extraction from X12 transactions"""
    
    @classmethod
    def setUpClass(cls):
        cls.fixtures_dir = Path(__file__).parent.parent / 'fixtures'
        cls.parser = X12Parser(log_level="ERROR")
    
    def test_member_metadata_extraction(self):
        """Test member information extraction from NM1*IL segment"""
        edi_file = self.fixtures_dir / 'test-x12-275.edi'
        
        if not edi_file.exists():
            self.skipTest(f"Test fixture not found: {edi_file}")
        
        result = self.parser.parse_file(str(edi_file))
        
        # subscriber identifier should be extracted from NM1 segment
        self.assertIn("member_id", result.metadata)
        self.assertTrue(len(result.metadata.get("member_id", "")) > 0)
    
    def test_payer_metadata_extraction(self):
        """Test payer information extraction from NM1*PR segment"""
        edi_file = self.fixtures_dir / 'test-x12-275.edi'
        
        if not edi_file.exists():
            self.skipTest(f"Test fixture not found: {edi_file}")
        
        result = self.parser.parse_file(str(edi_file))
        
        # Payer info should be extracted
        self.assertIn("payer_name", result.metadata)
        self.assertIn("payer_id", result.metadata)
    
    def test_trace_number_extraction(self):
        """Test trace number extraction from TRN segment"""
        edi_file = self.fixtures_dir / 'test-x12-275.edi'
        
        if not edi_file.exists():
            self.skipTest(f"Test fixture not found: {edi_file}")
        
        result = self.parser.parse_file(str(edi_file))
        
        # Trace number should be extracted
        self.assertIn("trace_number", result.metadata)


if __name__ == '__main__':
    unittest.main(verbosity=2)
