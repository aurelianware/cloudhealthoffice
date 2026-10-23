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

The library is pure: XML in, typed FHIR resources out. No storage, no HTTP, no
CHO service dependency, because the parse has three prospective callers — CDex
attachments, the 275/RFAI path, and a future document gateway — and belongs to
none of them.

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
