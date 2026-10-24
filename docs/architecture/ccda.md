# Reading C-CDA documents

Status: **v1 in progress — internal only.** Decided 23 October 2026.

## Why this exists

Providers already send CHO C-CDA documents. `CdexAttachmentPolicy` accepts
`application/hl7-cda+xml`, and those submissions land in the `cdex-attachments`
container keyed to the prior-authorization case they answer. Today they are
stored opaquely: a UM reviewer deciding an authorization sees a blob with a
filename.

This library reads them. Nothing more — it is the parse, not a pipeline.

## What v1 is, and is not

**v1 is internal only.** Parsed content is shown to UM reviewers alongside the
authorization. Nothing parsed here reaches a member-facing or CMS-regulated API,
and nothing is written to the clinical resource store.

That boundary is what makes v1 tractable, and it is worth being explicit about
why, because the three hard problems in this area are all problems of
*persistence* rather than of parsing:

- **Derived identity.** `PayerToPayerImportPolicy.ImportKey` hashes
  `tenant ␟ member ␟ source payer ␟ resource type ␟ source resource id`. A
  C-CDA has no source payer, and entry-level `id` is frequently absent, re-used
  across documents, or regenerated on every export — so a stored resource would
  need a content-hash fallback defined per resource type. Nothing is stored, so
  no identity is derived.
- **Origin.** `ClinicalResourceOrigin` has `Imported` (meaning "from another
  payer") and `ChoNative`. A hospital's discharge summary is neither, and the
  `meta.source` urn is wire-visible on a regulated API. Nothing reaches the
  wire, so the enum does not change.
- **The single-writer rule.** The only writer to the clinical store is
  Payer-to-Payer ingestion. This library writes nothing, so the rule holds
  untouched.

All three return when this is promoted to member-visible. They are deferred,
not solved.

## Shape

```
CcdaReader        is this a C-CDA, and what sections are in it?
CcdaValues        the CDA datatypes (CD/CE, IVL_TS, TS, PQ) as FHIR
CcdaCodeSystems   CDA code-system OIDs as FHIR canonical URIs
Ccda*Mapper       one section's entries as typed FHIR resources
```

Mapped so far: **Problems** (`Condition`), **Allergies**
(`AllergyIntolerance`), **Medications** (`MedicationRequest`). The remaining
six — Immunizations, Results, Vital Signs, Procedures, Plan of Treatment and
Care Team — are counted and preserved by the reader in the meantime.

The library is pure: XML in, typed FHIR resources out. No storage, no HTTP, no
CHO service dependency, because the parse has three prospective callers — CDex
attachments, the 275/RFAI path, and a future document gateway — and belongs to
none of them.

## How a reviewer sees it

The library parses; `document-service` is what puts the result on a screen.

```
GET /api/v1/documents/{id}/clinical-summary?subjectKind=&subjectId=
```

The endpoint passes exactly the gates `/content` passes — permission, tenant,
sensitivity tag, scan status — and writes the same `Download` audit entry,
allowed or refused. A summary is the document's contents under another name, so
anything that would stop a reviewer opening the bytes stops them reading the
summary. A document CHO reads no structure from (a PDF) answers 422 rather than
an empty summary that would read as "a C-CDA with nothing in it".

Parse-on-read. Nothing is cached or persisted, which is what keeps the three
deferred problems above deferred.

**The subject comes from CHO's own catalog link, never from the document.** A
C-CDA's `recordTarget` is the *sender's* identification of a person; matching on
it is a decision about identity, not a parse. A document filed against no member
still returns its sections, entry counts and narratives — those are what a
reviewer reads — but no resource, because a `Condition` attributed to nobody is
a clinical fact about an unnamed person. The response says `memberLinked: false`
so an empty list is legible as a filing problem rather than an empty document.

Each section carries both the FHIR resources and `items`, three plain fields
per entry (display, status, the one detail that changes a utilization decision)
so the portal never parses FHIR JSON to draw a table. Both are derived from the
coded entries; neither is derived from the narrative. The narrative travels
separately and the portal renders it **as text**: it is another organisation's
HTML arriving over an attachment channel.

A section CHO recognises but does not map is not an error. It is named, counted
and carries its narrative, and the panel marks it "Not read by CHO" — dropping
it would make the document look smaller than it is to somebody deciding an
authorization on it. The same applies to a mapped section whose entries carried
nothing codeable, which says so rather than rendering as empty.

`application/hl7-cda+xml` is on `DocumentUploadPolicy`'s allow list, signature-
checked as XML (prolog, with or without a BOM). XML has no magic number, so that
check only establishes the content is XML at all; content is served solely as a
download with `X-Content-Type-Options: nosniff`, so it is not an execution
vector.

## Rules the mappers follow

These are the rules that keep a parse from becoming a fabrication. They are
asserted in `tests/CloudHealthOffice.Ccda.Tests`, each with the failure it
prevents.

1. **No structured data is invented from narrative.** A section that is prose
   only stays prose. Entry counts are reported so a section whose narrative
   looks full and whose coded entries are absent is visible as such.
2. **No codes are inferred.** A medication without an RxNorm code becomes a
   resource without one. An unrecognised code system travels as `urn:oid:…`
   rather than a guessed canonical URI.
3. **Precision is preserved.** A year-only onset stays a year. Widening it to a
   day and a zone states facts nobody recorded — and onset is what a reviewer
   uses to decide whether a condition predates coverage.
4. **Sections are identified by templateId, never by title.** "Problems",
   "Problem List", "Active Problems" and "Diagnoses" are one section; a reader
   keyed on the words drops the wordings it has not seen.
5. **An unread section is named, counted and kept** — never dropped. A reviewer
   needs to know a document carried something CHO skipped, and its narrative is
   still readable.
6. **The patient is never resolved from the document.** A C-CDA's `recordTarget`
   is the sender's identification of a person; matching on it is a decision
   about identity, not a parse. Callers pass the member reference they have
   already established.
7. **A document that is not a C-CDA is refused, not read as far as it goes.**
   Section templateIds only mean what C-CDA says they mean.
8. **The target is FHIR R4.** R4 and R5 differ in ways that compile against the
   same shared datatypes — `MedicationRequest.medication[x]` is a choice in R4
   and a `CodeableReference` in R5 — so where a resource's shape differs
   between versions it is asserted against the serialized JSON rather than the
   object model.

### Where each mapper's real trap is

Worth knowing before writing the next one, because each section hides its
clinical meaning somewhere a reasonable reader would not look:

- **Problems.** Clinical status lives on the concern act, the diagnosis on the
  observation inside it. The diagnosis is `value`; `code` is what KIND of
  statement it is.
- **Allergies.** The substance is the participant's playing-entity code, NOT
  the observation's `value` (which is the kind of intolerance). Severity hangs
  off each reaction, not off the allergy — the same allergy can cause a rash
  once and anaphylaxis the next time.
- **Medications.** The element saying when a drug is TAKEN and the one saying
  when it was PRESCRIBED are both `effectiveTime`, told apart only by
  `xsi:type` and `operator="A"`. Senders write them in either order.

## Security

C-CDAs arrive from outside organisations. The reader prohibits DTD processing
and uses no XML resolver, so an external entity reference — which would
otherwise read this host's filesystem, or issue a request to a host of the
sender's choosing — is refused with the document.

Malformed input is an ordinary occurrence on this path, not a defect, so the
reader returns a refusal with a reason rather than throwing.

## Not in scope

- **No C-CDA generation.** One direction only.
- **No US Core profile conformance claim.**
- **No merging with payer-sourced data.** A C-CDA restates facts CHO may
  already hold; silently merging them destroys the provenance that makes the
  document worth having. De-duplication belongs in presentation, with a
  documented grouping key.
- **No CMS-0057-F row moves on this work.** It is worth doing for UM review and
  as the prerequisite for anything TEFCA, and it closes no gap in
  `docs/epa-gap-matrix.md` by itself.

## Testing

The mapping cannot be finished against specimens. Well-formed examples — the
HL7 CDA Examples IG, ONC's SITE certification data — are correct by
construction and are a floor to pass, not a test. Hardening needs real exported
documents from the EHRs CHO's providers actually use, including the badly
behaved ones; the best source is CHO's own `cdex-attachments`, de-identified.
