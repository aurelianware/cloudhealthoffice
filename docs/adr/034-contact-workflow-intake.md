# ADR 034: contact-owned intake intents and owner-owned cases

Status: Proposed. Date: 2026-10-25.

Contacts retain an immutable intake intent before calling the owning service.
The owner reads that intent from contact-service using the acting user's token;
no caller-supplied receipt timestamp is authoritative. The contact start time
anchors the case clock. Destination identities are tenant/contact scoped, so a
retry after a lost response cannot create a second case or follow-up.

Member grievance and appeal intake uses a separate collection in appeals-service:
legacy claim appeals require claim/provider fields and must not receive fabricated
values. This increment admits member-self cases only; representative authority,
provider disputes, notices, extensions and full downstream effectuation remain
separate. Coverage intake creates a native authorization from explicit clinical
fields and refuses external-core modes. Existing decision paths remain authoritative.

Failed delivery leaves the intent visible and contact closure blocked. The user
retries with current permission; no bearer token is stored and no background worker
impersonates the user. Immutable member/classification/intake identity prevents
later edits from changing the meaning of an accepted or pending action.

Follow-ups remain Inquiry work owned by work-management. Their explicit request
key is tenant/user scoped, content-bound and insert-only. A new key means a new
follow-up; retries never reopen or overwrite completed work.

Document Call subjects use service:read, matching contact-service. Malware scanning
uses a deployment-selected ClamAV INSTREAM client; only a complete clean verdict
permits opening content. Scanner failure never becomes clean. Scanning deployment
must use a private trusted network and an updated signature database.
