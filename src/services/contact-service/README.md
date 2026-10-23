# contact-service

Call tracking: the record of a call, chat, email or walk-in, what it was
about, and what it touched.

Design of record: [docs/architecture/cho-service.md](../../../docs/architecture/cho-service.md).
This service is **step 1** of that document's build order.

## What it owns

- **The contact** (`Models/Contact.cs`) — channel, direction, caller, the
  verified member or provider, timings, reasons, linked subjects, the
  classification, the resolution and an append-only history.
- **The tenant's reason taxonomy** (`Models/ContactReason.cs`) —
  configuration, not an enum, because call volume by reason is the spine of
  service reporting and every plan counts differently. A starter list is
  seeded for a tenant that has none.
- **Subject linking** — linking a claim also writes its `MemberContact` or
  `ProviderContact` note through claims-service, so the claim's own history
  shows the call.

## What it does not own

- **Telephony.** CHO does not route calls (cho-service.md §6.2). The contact
  keeps the platform's call id and a link to its recording, nothing more.
- **Cases.** Grievances and appeals live in appeals-service, coverage
  requests in authorization-service. Step 1 records the rep's
  classification; opening the case dated from the call, and running its
  rule-pack clock, is step 2.
- **Follow-up work items.** Work management owns queues and Get Next. The
  contact carries `followUpWorkItemIds`; filling it is step 3.

## Permissions

| Permission | What it allows |
|---|---|
| `service:read` | Read contacts and the reason taxonomy. |
| `service:contact` | Start, work and close a contact. |
| `service:admin` | Configure the reason taxonomy. |

MemberServices and ProviderRelations hold the first two. Compliance reads
contacts through `*:read`. Writing a claim note needs the rep's own
`claims:note`: the note is attributed to the person who took the call, not
to this service, so their token is forwarded rather than a service identity.

## Configuration

| Key | Default | Notes |
|---|---|---|
| `MongoDb:ConnectionString` | — | Required; the host refuses to start without it. |
| `MongoDb:ContactsCollection` | `Contacts` | |
| `MongoDb:ContactReasonsCollection` | `ContactReasons` | |
| `Services:ClaimsServiceUrl` | — | Unset, a linked claim is still recorded on the contact; only the claim's own history misses the call. |

## Running the tests

```
dotnet test src/services/contact-service/ContactService.Tests/ContactService.Tests.csproj
```
