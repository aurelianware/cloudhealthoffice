#!/usr/bin/env python3
"""Simulate the x12-278 ingest workflow against the fixture EDI file."""

import json
import shutil
import tempfile
import unittest
from pathlib import Path

# Ensure the parser module is available on the path
CONTAINERS_PATH = Path(__file__).resolve().parent.parent.parent / 'containers' / 'x12-parser'
if CONTAINERS_PATH.exists():
    import sys

    sys.path.insert(0, str(CONTAINERS_PATH))

from parse_x12 import X12Parser


class TestX12278WorkflowSimulation(unittest.TestCase):
    """Exercise key stages of the Argo x12-278 ingest workflow."""

    @classmethod
    def setUpClass(cls):
        cls.fixtures_dir = Path(__file__).parent.parent / 'fixtures'
        cls.parser = X12Parser(log_level="ERROR")

    def test_simulated_workflow_happy_path(self):
        edi_source = self.fixtures_dir / 'test-x12-278.edi'
        self.assertTrue(edi_source.exists(), f"Fixture missing: {edi_source}")

        with tempfile.TemporaryDirectory() as tmpdir:
            staging_dir = Path(tmpdir) / 'sftp-fetch'
            staging_dir.mkdir()
            staged_file = staging_dir / edi_source.name
            staged_file.write_bytes(edi_source.read_bytes())

            # Step 2: Parse the staged EDI payload
            result = self.parser.parse_file(str(staged_file))
            self.assertEqual(result.transaction_type, '278')
            self.assertEqual(len(result.parse_errors), 0, f"Parse errors detected: {result.parse_errors}")

            metadata = result.metadata
            for field in ['trace_number', 'member_id', 'provider_npi', 'um_request_category', 'review_action_code']:
                self.assertTrue(metadata.get(field), f"Missing required metadata field: {field}")

            # Step 4: Build the payload our claims backend expects
            backend_payload = {
                'authorization_id': metadata['trace_number'],
                'member': {
                    'member_id': metadata['member_id'],
                    'first_name': metadata.get('member_first_name'),
                    'last_name': metadata.get('member_last_name'),
                },
                'provider': {
                    'name': metadata.get('provider_name'),
                    'npi': metadata['provider_npi'],
                },
                'service_window': metadata.get('service_date'),
                'review': {
                    'action_code': metadata['review_action_code'],
                    'reason_code': metadata.get('review_reason_code'),
                    'request_category': metadata['um_request_category'],
                    'certification_type': metadata.get('um_certification_type'),
                },
            }
            self.assertTrue(backend_payload['authorization_id'])
            self.assertTrue(backend_payload['review']['action_code'])

            # Step 5: Archive the raw file (simulate S3 upload by copying locally)
            archive_dir = Path(tmpdir) / 'archive' / 'raw' / '278'
            archive_dir.mkdir(parents=True, exist_ok=True)
            archived_file = archive_dir / staged_file.name
            shutil.copy2(staged_file, archived_file)
            self.assertTrue(archived_file.exists(), 'Raw EDI file was not archived')
            self.assertEqual(archived_file.read_bytes(), staged_file.read_bytes())

            # Step 6: Produce the Kafka payload
            kafka_payload = {
                'topic': 'edi-278',
                'authorization_id': metadata['trace_number'],
                'metadata': metadata,
            }
            serialized = json.dumps(kafka_payload)
            self.assertIn('edi-278', serialized)
            self.assertIn(metadata['member_id'], serialized)


if __name__ == '__main__':
    unittest.main(verbosity=2)
