# X12 276 Claim Status Inquiry Parser

Parses HIPAA X12 276 (005010X212) transactions into structured JSON.

## Transaction Type

**276** - Health Care Claim Status Request

Sent by providers/clearinghouses to payers to inquire about claim processing status.

## Usage

### Docker

```bash
# Build
docker build -t ghcr.io/aurelianware/cloudhealthoffice-x12-276-parser:latest .

# Run
docker run -v $(pwd)/test-data:/data \
  ghcr.io/aurelianware/cloudhealthoffice-x12-276-parser:latest \
  /data/test-276.edi --output /data/output-276.json
```

### Python

```bash
python parse-276.py input.edi --output output.json
```

## Output Structure

```json
{
  "file_name": "test-276.edi",
  "isa_envelope": {
    "sender_id": "CLEARINGHOUSE",
    "receiver_id": "BCBSFLORIDA",
    "interchange_date": "20260208",
    "interchange_time": "1430"
  },
  "inquiries": [
    {
      "transaction_set_control_number": "0001",
      "information_source": {
        "entity_identifier": "PR",
        "last_name_or_org": "BLUE CROSS BLUE SHIELD",
        "id_code": "66917"
      },
      "information_receiver": {
        "entity_identifier": "1P",
        "last_name_or_org": "SAMPLE MEDICAL CENTER",
        "id_code": "1234567890"
      },
      "subscriber": {
        "last_name": "SMITH",
        "first_name": "JOHN",
        "member_id": "MEM123456",
        "date_of_birth": "19800515"
      },
      "claims": [
        {
          "claim_number": "CLM987654321",
          "service_date_from": "20260115",
          "service_date_to": "20260115",
          "total_claim_charge": "250.00"
        }
      ]
    }
  ],
  "parse_errors": [],
  "parsed_at": "2026-02-08T14:30:00Z"
}
```

## Integration

Used in Argo Workflow `x12-276-ingest` to parse inbound claim status inquiries.
