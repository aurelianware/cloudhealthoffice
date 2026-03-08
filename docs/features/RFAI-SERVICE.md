# RFAI Service

**Status**: ✅ Phase 1 MVP  
**Transaction**: HIPAA X12 277 RFAI (Request for Additional Information)  
**Availability Auth Workflow**: Availity / Cognizant  

---

## Overview

The **RFAI Service** manages cases where a payer has requested additional clinical documentation
before adjudicating a prior-authorization or claim.  A case is created when the authorization
workflow determines that attachments are needed (e.g. operative reports, lab results).

Once the **Attachment Service** receives and links an inbound 275 transaction to a case, it
notifies this service via `POST /api/rfai/{id}/attachments/received`, transitioning the case
status from `Open` → `DocsReceived`.

### Key concepts

| Concept | Description |
|---------|-------------|
| **Authorization number** | `TRN02` value from the 278 response; uniquely identifies the prior-auth |
| **Requested items** | One or more document types the payer requires (e.g. operative report, pathology) |
| **Received attachments** | Inbound 275 attachments correlated to this RFAI case |
| **Status flow** | `Open` → `DocsReceived` → `Closed` / `Cancelled` |

---

## Data Model

```jsonc
{
  "id": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "tenantId": "tenant-abc",
  "authNumber": "AUTH20240115001",           // alphanumeric, from 278 TRN02
  "status": "Open",                          // Open | DocsReceived | Closed | Cancelled
  "requestedItems": [
    { "code": "OZ", "description": "Operative report", "required": true },
    { "code": "CT", "description": "CT scan images",   "required": false }
  ],
  "receivedAttachments": [],
  "dueDate": "2026-04-01T00:00:00Z",
  "notes": "Requested by Availity payer gateway",
  "createdAt": "2026-03-08T13:00:00Z",
  "updatedAt": "2026-03-08T13:00:00Z"
}
```

---

## Endpoints

### `POST /api/rfai` — Create an RFAI case

**Request**

```json
{
  "tenantId": "tenant-abc",
  "authNumber": "AUTH20240115001",
  "dueDate": "2026-04-01T00:00:00Z",
  "requestedItems": [
    { "code": "OZ", "description": "Operative report", "required": true }
  ],
  "notes": "Requested via Availity gateway"
}
```

**Response** `201 Created`

```json
{
  "id": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "tenantId": "tenant-abc",
  "authNumber": "AUTH20240115001",
  "status": "Open",
  "requestedItems": [
    { "code": "OZ", "description": "Operative report", "required": true }
  ],
  "receivedAttachments": [],
  "dueDate": "2026-04-01T00:00:00Z",
  "notes": "Requested via Availity gateway",
  "createdAt": "2026-03-08T13:00:00Z",
  "updatedAt": "2026-03-08T13:00:00Z"
}
```

**Validation**
- `tenantId` — required, non-empty
- `authNumber` — required, alphanumeric only
- Each `requestedItems[].description` — required, non-empty

---

### `GET /api/rfai/{id}` — Get an RFAI case by ID

```
GET /api/rfai/3fa85f64-5717-4562-b3fc-2c963f66afa6
X-Tenant-ID: tenant-abc
```

**Response** `200 OK` — returns the full `RfaiCase` object (see above).  
**Response** `404 Not Found` — when the case does not exist for the requesting tenant.

---

### `GET /api/rfai/by-auth/{tenantId}/{authNumber}` — List cases by authorization number

Returns all cases (newest first) for the given tenant and authorization number.

```
GET /api/rfai/by-auth/tenant-abc/AUTH20240115001
```

**Response** `200 OK`

```json
[
  {
    "id": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
    "status": "Open",
    ...
  }
]
```

---

### `POST /api/rfai/{id}/attachments/received` — Record a received attachment

Called by **attachment-service** when an inbound 275 has been linked to this case.

**Request**

```json
{
  "receivedAt": "2026-03-08T14:30:00Z",
  "attachmentControlNumber": "ACN20240115001",
  "storageProvider": "azure-blob",
  "storageKey": "hipaa-attachments/raw/275/2026/03/08/report.edi",
  "fileHash": "sha256:abc123...",
  "sourceTransaction": {
    "transactionSetId": "275",
    "gsControl": "12345",
    "stControl": "0001"
  }
}
```

All fields are optional. If `receivedAt` is omitted it defaults to `UtcNow`.

**Behavior**
- Appends the record to `receivedAttachments`.
- If `status` is `Open`, transitions it to `DocsReceived`.

**Response** `200 OK` — returns the updated `RfaiCase`.  
**Response** `404 Not Found` — when the case does not exist for the requesting tenant.

---

## Configuration

| Environment variable | Description | Default |
|---|---|---|
| `MongoDb__ConnectionString` | MongoDB connection string (required) | — |
| `MongoDb__DatabaseName` | MongoDB database name | `CloudHealthOffice` |
| `ASPNETCORE_ENVIRONMENT` | `Development` / `Production` | `Production` |

---

## Running locally

```bash
# Set the connection string (local MongoDB or Atlas dev cluster)
export MongoDb__ConnectionString="mongodb://admin:password@localhost:27017"

cd src/services/rfai-service
dotnet run
# Swagger UI → http://localhost:5000
# Health check → http://localhost:5000/health
```

---

## Out of scope (Phase 2+)

- Generating outbound 277 RFAI EDI transactions
- 824 Application Advice generation
- Full 275 parsing and auto-correlation rules
