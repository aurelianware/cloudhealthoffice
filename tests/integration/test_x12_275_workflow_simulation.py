#!/usr/bin/env python3
"""Simulate the x12-275 ingest workflow against the fixture EDI file."""

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


class TestX12275WorkflowSimulation(unittest.TestCase):
    """Exercise key stages of the Argo x12-275 ingest workflow."""

    @classmethod
    def setUpClass(cls):
        cls.fixtures_dir = Path(__file__).parent.parent / 'fixtures'
        cls.parser = X12Parser(log_level="ERROR")

    def test_simulated_workflow_happy_path(self):
        edi_source = self.fixtures_dir / 'test-x12-275.edi'
        self.assertTrue(edi_source.exists(), f"Fixture missing: {edi_source}")

        with tempfile.TemporaryDirectory() as tmpdir:
            staging_dir = Path(tmpdir) / 'sftp-fetch'
            staging_dir.mkdir()
            staged_file = staging_dir / edi_source.name
            staged_file.write_bytes(edi_source.read_bytes())

            # Step 2: Parse the staged EDI payload
            result = self.parser.parse_file(str(staged_file))
            self.assertEqual(result.transaction_type, '275')
            self.assertEqual(len(result.parse_errors), 0, f"Parse errors detected: {result.parse_errors}")

            # Step 3 & 4: Build the metadata payload the workflow would emit
            metadata = {
                'transaction_type': result.transaction_type,
                'trace_number': result.metadata.get('trace_number'),
                'member_id': result.metadata.get('member_id'),
                'attachment_control_number': result.metadata.get('attachment_control_number'),
                'payload_size': staged_file.stat().st_size,
            }
            for key in ['transaction_type', 'trace_number', 'member_id']:
                self.assertTrue(metadata[key], f"Missing required metadata field: {key}")

            # Step 5: Archive the raw file (simulate S3 upload by copying locally)
            archive_dir = Path(tmpdir) / 'archive' / 'raw' / '275'
            archive_dir.mkdir(parents=True, exist_ok=True)
            archived_file = archive_dir / staged_file.name
            shutil.copy2(staged_file, archived_file)
            self.assertTrue(archived_file.exists(), 'Raw EDI file was not archived')
            self.assertEqual(archived_file.read_bytes(), staged_file.read_bytes())

            # Step 6: Produce the Kafka payload
            kafka_payload = {
                'topic': 'attachments-in',
                'filename': staged_file.name,
                'metadata': metadata,
            }
            serialized = json.dumps(kafka_payload)
            self.assertIn('attachments-in', serialized)
            self.assertIn(metadata['trace_number'], serialized)


if __name__ == '__main__':
    unittest.main(verbosity=2)
