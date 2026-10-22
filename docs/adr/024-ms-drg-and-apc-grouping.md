# ADR 024: MS-DRG and APC Grouping

## Status

Proposed. Awaiting a decision on option A vs B vs C in section 4.

## Context

Cloud Health Office cannot price an inpatient or an outpatient facility claim
the way an incumbent payer platform does, because it has no grouper. Two
things are missing and they are different problems.

### 1. Nothing assigns a group

`RepricingService.RepriceClaimAsync` says so in a comment and refuses the
claim:

```csharp
if (loaded.Schedule.Type == EngineDomain.FeeScheduleType.Drg && string.IsNullOrEmpty(request.DrgCode))
{
    // Cloud Health Office has no MS-DRG grouper: an inpatient claim without a DRG cannot be priced.
    warnings.Add("No DRG code provided. ...");
```

`PricingStage` (claims-service) takes the DRG straight off the claim —
`claim.Institutional?.DrgCode`, which is 837I `HI*DR`, the DRG the *facility*
billed. The payer therefore pays whatever group the hospital asserted. No
incumbent does this: QNXT, Facets and HealthRules all regroup the claim from
diagnoses and procedures and pay their own group, which is the only way to
catch upcoding and the only way to pay a claim that arrives without a DRG.

On the outpatient side there is no APC assignment at all. `FeeScheduleType`
has a `MedicareOpps` member whose doc comment reads "CHO stores pre-calculated
APC rates", and `FeeScheduleLoaderService` does read the APC and status
indicator per HCPCS out of the OPPS addendum into `FeeScheduleEntry`. But that
is a code-level lookup, not grouping: nothing assigns an APC at the claim
level, and the status indicator is consulted in exactly one place —
`PricingScheduleSources.cs:206`, to flag status T for the multiple-procedure
reduction. There is no packaging (status N), no conditional packaging
(Q1/Q2/Q3), no comprehensive APC (J1/J2), and no composite APC. An OPPS claim
today prices as a bag of independent line rates, which overpays every packaged
line.

### 2. Nothing applies the IPPS payment adjustments

Even given a DRG, the IPPS payment is not `weight × base rate`. It is

```
operating = DRG weight x (labor share x wage index + non-labor share x COLA)
          + IME + DSH (+ uncompensated care)
capital   = DRG weight x capital federal rate x geographic adjustment factor
          + capital IME + capital DSH
payment   = operating + capital + outlier + new technology add-on
            adjusted for transfer policy and post-acute transfer policy
```

`RateResolutionService` implements only `DrgWeight x DrgBaseRate` (or a flat
case rate). There is no wage index, no outlier, no transfer per-diem, no
IME/DSH. A short-stay transfer is currently paid the full case rate, which is
the single largest overpayment a payer platform can have.

### 3. What the claim model already carries

The grouper inputs are almost all present, which is why this is tractable:

| Grouper input | Where it is today |
| --- | --- |
| Principal and secondary diagnoses | `Claim.DiagnosisCodes[]` (`CodeQualifier` ABK / ABF) |
| Present-on-admission | `DiagnosisCode.PresentOnAdmission` (Y/N/U/W/1) |
| ICD-10-PCS procedures | `InstitutionalClaim.PrincipalProcedure`, `.OtherProcedures[]` |
| Discharge status | `InstitutionalClaim.PatientStatusCode` |
| Sex, birth date | `Claim` subscriber/patient demographics |
| Length of stay | `InstitutionalClaim.CalculateLengthOfStay()` |
| Type of bill | `InstitutionalClaim.ComposeTypeOfBill(...)` |
| Revenue codes, HCPCS, units, modifiers | `ClaimServiceLine` |
| Billed DRG (to compare against) | `InstitutionalClaim.DrgCode` |

Missing and needed: discharge hour is there but admission hour is not used;
the facility's CCN/provider-specific file (wage index, CBSA, IME ratio, DSH
percentage, cost-to-charge ratio) has no home in the data model at all.

## What CMS publishes

This decision turns on what is actually available, so the sources are listed
explicitly. All of it is free and public; none of it requires a vendor
licence.

| Artefact | What it gives | Form |
| --- | --- | --- |
| [MS-DRG Classifications and Software](https://www.cms.gov/medicare/payment/prospective-payment-systems/acute-inpatient-pps/ms-drg-classifications-and-software) | The grouper itself. CMS posts, per version (now V44, FY 2027), the **MS-DRG and MCE Java source code and reference-implementation binaries**, mainframe software, and the ICD-10 MS-DRG Definitions Manual. | Java source (for transparency; CMS states it is "not intended for direct use in applications") plus precompiled jars that "can be used on any platform running Java" |
| ICD-10 MS-DRG Definitions Manual (same page) | MDC assignment, the DRG list, the CC and MCC lists, the CC exclusion list, the OR and non-OR procedure lists — i.e. every table the grouper logic walks | Text / HTML tables |
| [Pricer Source Code and Software](https://www.cms.gov/pricersourcecodesoftware) | Ten Java Pricer applications including **IPPS** and **OPPS**. Executable jars price claims; source is reference only and excludes `gov.cms.fiss.pricers.common`. First Java-only IPPS release was October 2021 (FY 2019 forward). | Executable Java jars |
| [I/OCE software](https://www.cms.gov/medicare/coding-billing/integrated-outpatient-code-editor/integrated-outpatient-code-editor-i-oce-software) and [quarterly release files](https://www.cms.gov/medicare/coding-billing/outpatient-code-editor-oce/quarterly-release-files) | APC assignment, status indicators, packaging, discounting and OPPS line-level edits. October 2026 is V27.3 (data files plus mainframe, PC and **Java standalone** versions); the specification is published as a transmittal (R13979). | Java standalone, plus data files |
| OPPS Addenda A and B (quarterly, OPPS final rule) | APC rate per APC, and HCPCS → APC + status indicator | CSV / Excel |
| IPPS Table 2 to 5, wage index tables, provider-specific file | DRG weights and geometric/arithmetic mean LOS, wage index by CBSA, per-provider IME/DSH/CCR | CSV / Excel |

Two constraints come with the software: CMS gives **no technical support** for
using the grouper, Pricer or I/OCE outside Original Medicare claims
processing, and the jars are Java (8 today, moving to 17; Java 8 support ends
November 2026). A .NET platform cannot link them in-process.

### What is actually in the V44 definitions manual download

Checked rather than assumed, because option A stands or falls on whether the
tables are machine-readable. `fy2027-fr-icd10-ms-drg-definitions-manual-files-v44.zip`
(4.5 MB compressed, 67 MB of fixed-width text) holds nine files, and each one
maps onto a table in section 5 step 2:

| File | Holds | Maps to |
| --- | --- | --- |
| `appendix_A.txt` | 779 lines: every MS-DRG with its MDC and an `M`/`P` medical-or-surgical flag. Pre-MDC DRGs have the MDC column blank, which is how they identify themselves. | `MsDrgDefinition` (code, title, MDC, partition) |
| `appendix_B.txt` | 90,817 lines: each ICD-10-CM code with its MDC and the MS-DRG range it defines the logic of | `MdcAssignment` |
| `appendix_C.txt` | 209,778 lines, in three parts: the CC and MCC lists with each code's HAC category, the codes that are an MCC only for patients discharged alive, and the codes excluded from acting as a CC/MCC within named DRGs. Each CC row names its principal-diagnosis exclusion group. | `ComplicationAssignment` and `CcExclusion` |
| `appendix_D_E.txt` | The surgical hierarchy, MDC by MDC, as an ordered list — MDC 08 runs from extensive spinal fusion down to major joint replacement at position 23 | `SurgicalClassAssignment.HierarchyRank` |
| `appendix_F_I.txt` | The unrelated-O.R.-procedure DRGs (981-989) and their severity tables | `DrgFamilyRule` |
| `mdcs_00_07.txt` … `mdcs_22_25.txt` | Per-MDC logic: the diagnosis codes assigned to the MDC, then each surgical class with its procedure codes | `MdcAssignment`, `SurgicalClassAssignment` |

Two things fall out of this. The surgical hierarchy really is a plain ranked
list, so modelling it as a rank per class is not a simplification. And the
hierarchy entries are DRG ranges rather than named classes, so the importer
derives the class key from the range — `469-470` is one class, not two.

A detail the manual settles: in V44 the 469/470 family is titled "… or Total
Ankle Replacement", not the FY 2018 wording. The seed slice carries the V44
titles verbatim so a reviewer can diff them against Appendix A.

### What the V44 jars turned out to contain

The jars were opened rather than reasoned about, and they change the design
more than the Appendix inventory above did. `tools/cms-grouper-table-extractor`
reads them; its README has the detail. Three findings matter here.

**The tables are a protobuf buffer, readable with two jars.** They live in
`runtime_buffers/msdrg_combined.bin` inside `msdrg-binary-access`, described by
the release's own generated classes. Reading it needs that jar and
`protobuf-java` — not the grouper itself, and not the Solventum (former 3M)
foundation library the grouper's runtime options extend. That is why the
extractor reads the buffer rather than calling the grouper.

**Running CMS's grouper as the parity oracle is also feasible**, which was in
doubt for an hour and is worth recording as settled. The foundation library the
grouper needs is not on Maven Central under any obvious coordinates, and the
first conclusion here was that it shipped only inside the release's 278 MB
Windows installer. It does not: the release's own Java API guide names it as a
*Solventum open source dependency*, published at
[solventum-oss/GFC-Grouper-Foundation-Classes](https://github.com/solventum-oss/GFC-Grouper-Foundation-Classes).
The full set to run the grouper is therefore:

| Dependency | Version | Where from |
| --- | --- | --- |
| `msdrg-core`, `msdrg-model-v2`, `msdrg-binary-access` | 44.0.0.3 / 2.12.2 / 1.6.4 | the CMS Java jars download |
| `gfc-base-api` (`com.mmm.his.cer.gfc.gfc-api`) | 3.4.9 | **not needed — see below** |
| `protobuf-java` | 3.25.5 | Maven Central |
| `slf4j-api` | 1.7.36 | Maven Central |

plus a Java 17 or newer runtime. The guide also documents the calling sequence
(`MsdrgRuntimeOption` → `RuntimeOptions` → `MsdrgInput.builder()` →
`MsdrgComponent.process(claim)` → `claim.getOutput()`), so the oracle harness is
a known quantity rather than reverse engineering.

#### The foundation library turned out not to be needed at all

`gfc-base-api` has no published binary — a search of Maven Central for
`gfc-base-api` returns nothing — so the plan was for the parity job to build it
from the Solventum source. It does not have to. The CMS jars reference exactly
five types from that library, and the required member set is readable straight
out of their bytecode constant pools:

| Type | What the jars actually use |
| --- | --- |
| `GfcPoa` | the constants `Y N U W E ONE BLANK INVALID`, and `toString()` |
| `ComponentRuntime` | the no-arg constructor, then `Map.get` and `Map.containsKey` |
| `Claim` | the no-arg constructor |
| `FoundationException` | `(String)` and `(String, Throwable)` |
| `FoundationRuntimeException` | `(String)` |

plus `Processable`, `ComponentName`, `ComponentPackage`, `ComponentVersion` and
`IClaim`, which appear only in signatures. `tools/cms-grouper-parity-oracle/`
implements that much and nothing more, and the grouper runs.

A shim in an oracle would normally be unacceptable, because an oracle whose own
code decides anything is worthless. This one decides nothing, and that is
checkable rather than asserted: there is no `Class.forName` anywhere in the
jars, so no code path is selected by this code; `ComponentRuntime` is read only
through `Map` methods, so a `HashMap` subclass reproduces it entirely; and
`GfcPoa.toString()` is called from one place, `TraceUtility`, which writes trace
text and never reaches grouping. Every grouping decision stays in CMS's
compiled classes driven by CMS's own table buffer, and a shim wrong in any way
that mattered would fail to link rather than group quietly to a wrong answer.

The corpus still moves to the front of the build order below, for the second
reason given there: the evaluator is much easier to write against a corpus that
can answer "what does CMS say for this stay?" on demand.

Extracted from V44: 75,005 diagnosis codes, 83,558 procedure codes, 4,111
procedure clusters, formula lists for 28 MDCs, the CC and PDX exclusion groups,
and the HAC categories and their logic — 16 tables, about 30 MB of JSON.

**CMS's grouping logic is boolean formulas over code attributes, not rule
rows.** Each MDC holds a ranked list of entries carrying a base DRG, a DRG, a
surgical flag, a re-route MDC, a severity, and an expression:

```
MDC 8, rank 3:   drg 525  base 523  SURGICAL   fuse9plus | cmadifd | tulip
MDC 8, rank 74:  drg 519  base 518  SURGICAL   (CC & backneck)
MDC 5, rank 16:  drg 234  base 233  SURGICAL   (sensor | NORcardcath) & bypass
MDC 5, rank 87:                                (ANY:ami) & (ami)
```

The operands are attributes attached to codes (`I509` carries `nbmaj`;
`0SRC0J9` carries `replacex`, `rightknee`, `or_allowed_in_768` and five more);
`CC` and `MCC` are the resolved severity; `ANY:` scopes an operand across the
claim. `msdrg-core` ships a lexer and parser for the expression language. The
logic is data, which is the strongest possible argument for this ADR's
approach — and it means the rule-row model in step 1's `MsDrgTableSet` is a
simplification that will not reach parity. **The grouper step is therefore a port of
CMS's evaluator, not an extension of the current rule matcher**: code →
attributes, a ranked formula list per MDC, and an expression evaluator. The
surgical hierarchy then falls out of `rank`, MDC re-routing out of
`reRouteMdcId`, and CC exclusion out of `severitySuppressionOperand` and the
exclusion groups, rather than each being modelled by hand.

**One extract covers nine versions.** Every row names the versions it applies
to (`V400` to `V440`) and each version has its own hierarchy rank, so grouping
a stay by discharge date is satisfiable from a single extracted set. That is
what `MsDrgTableSet`'s effective-date window was built for, and it survives
unchanged.

## Decision

### What other payer platforms actually do

Checked because "build or buy" should be answered against what the market
does, not from first principles.

**None of the incumbents wrote a grouper.** QNXT and Facets both price
through the same component, Cognizant's TriZetto NetworX Pricer, which is
marketed as covering 350-plus pricing calculations including DRG, APC, APG
and APR-DRG — and which
[integrates with Medicare and Medicaid third-party grouper technology](https://www.cognizant.com/en_us/trizetto/documents/cognizant-trizetto-networx-pricer.pdf)
rather than containing one. The grouper comes from a partner: Cognizant's
[Facets partner page](https://www.cognizant.com/trizetto/partners/facets)
names Optum's ECMPro as integrating with NetworX for grouping, pricing and
editing across the federal PPS models, and
[Micro-Dyn](https://www.microdynmedical.com/drgactive-ippsgrouper) sells
DRGActive and PRICERActive as QNXT integrations. HealthRules appears the
same way, as a target in grouper vendors' integration lists.

So the architecture this ADR proposes — a grouping seam with a licensed or
reference grouper behind it — is not a workaround for being smaller than the
incumbents. It is what the incumbents are. The difference worth having is
shipping a working grouper behind the seam by default, where they ship an
integration point and a purchase order.

**A native port of the CMS grouper is proven, not speculative.** The one
publicly documented attempt,
[a Zig port with Python bindings](https://docs.bedrockbilling.com/blog/rewriting-cms-grouper-in-zig/),
reports a line-by-line port validated against the CMS Java application over
more than 50,000 reference claims at a 100% output match, across MS-DRG
assignment, MDC routing and the HAC modifiers. Two numbers from it matter
here:

| | Claims per second |
| --- | --- |
| CMS Java grouper (their measurement, standard laptop) | ~500 |
| Their native port | >11,000 |

That is the case against option B on its own terms. 500 claims per second,
plus JVM warmup and a heap full of reference-data hash maps, is not something
to put on the adjudication hot path.

**It also changes step 2 below.** They did not parse the definitions-manual
text; they extracted the logic and tables out of the CMS jars (through JPype)
into intermediate files, then compiled those. That is the better source: the
jars are what CMS actually runs, where the Appendix text is a rendering of
it. The importer should take the jars as its source of truth and use the
Appendix files as a cross-check, not the other way round.

They also flag something this ADR had not accounted for: ICD-10 codes change
every 1 October, so a claim from an earlier fiscal year has to be
forward-mapped with the CMS conversion tables before it can be grouped under
a later version. Grouping by discharge date (which the table set already
does) covers the normal case; re-grouping an old stay under a current version
does not, and needs those tables.

**Commercial grouper pricing is quote-only.** Solventum (the former 3M
health information business) licenses
[Grouper Plus Content Services](https://www.solventum.com/en-us/home/health-information-technology/solutions/gpcs/)
modularly — "a single clinical classification methodology or a combination" —
to providers, payers and software partners, and publishes no price. Optum
does the same with EASYGroup. No honest cost figure can go in this ADR
without a quote, so option C's cost is stated as a structure (per
methodology, recurring) and not a number.

**Outpatient is a two-method world, and only one method is free.** Medicare
APC is public: the I/OCE and the OPPS addenda cost nothing. The commercial
alternative is Solventum's
[EAPG](https://assets.solventum.com/is/content/mmmspinco/eapg-classification-system-factsheet-pdfa-his-pm-enpdf),
which bundles at the visit level where APC pays per code, is common in
Medicaid, and is used by a number of commercial payers — DC Medicaid's FAQ
has named BCBS of Oklahoma, Minnesota and Alabama and Wellmark among them,
and Solventum's own material claims roughly 20 major payers, which is
vendor-reported. UnitedHealthcare uses its own Outpatient Procedural Groupers
instead. A payer platform therefore needs APC natively and EAPG as a licensed
plug-in; it cannot have EAPG any other way.

### 4. The three options

**Option A — pluggable interface, native .NET grouper, CMS jar as the parity
oracle (recommended).**

A new `CloudHealthOffice.GrouperEngine` defines the contracts
(`IMsDrgGrouper`, `IApcAssigner`, `IIppsPaymentCalculator`) and ships a
table-driven .NET implementation whose tables are imported from the CMS
Definitions Manual, the DRG weight tables and OPPS Addendum B — the same
`Import/` + `Persistence/` + `Data/` shape `CloudHealthOffice.NcciEngine`
already uses for the quarterly NCCI files. The CMS Java grouper and OPPS
Pricer jars are packaged as an optional sidecar container behind a second
implementation of the same interfaces, used two ways: as the authoritative
provider for a customer who wants bit-exact CMS parity, and as the **test
oracle** in CI, where a corpus of synthetic stays is grouped by both and the
build fails on disagreement.

- Pays for itself: grouping is in-process, no JVM in the hot path, versioned
  by discharge date like every other reference table in the platform.
- Provable: "our grouper agrees with CMS V44 on N synthetic stays" is a
  diligence answer; "we wrote a grouper" is not.
- Cost: the MS-DRG logic is real work — MDC assignment, pre-MDC cases,
  CC/MCC with the exclusion list, OR-procedure surgical hierarchy. Estimate
  four to six PRs for inpatient and three for OPPS.

**Option B — CMS jars only.** Ship the grouper, I/OCE and Pricer jars as a
required sidecar and make the .NET side a thin HTTP client. Fastest to exact
parity and no grouping logic to own, but it puts a JVM on the critical path
of adjudication, inherits the Java 8 → 17 migration and the quarterly
re-packaging, makes the platform unable to group a non-Medicare (commercial
or Medicaid) grouping variant, and leaves nothing to point at as our own
capability. CMS also supports none of it outside their own claims system.

**Option C — interface only, adapter for a licensed vendor.** Define the
interfaces, ship no grouper, and integrate 3M/Solventum or Optum EASYGroup
under licence (also the only realistic route to APR-DRG and EAPG, which
Medicaid lines of business need). Cheapest to build and what many payers
actually do, but it is a per-member/per-claim licence cost, a vendor
dependency in diligence, and it leaves the platform unable to price anything
out of the box.

### 5. Recommendation

Option A, with option C shipped as a first-class deployment rather than a
fallback — which the market evidence above sharpens in three ways.

1. **The seam is not optional under any option**, and it is what the
   incumbents sell. Build it first (done), so a customer who already licences
   EASYGroup or GPCS plugs it in, and one who wants bit-exact CMS parity runs
   the sidecar.
2. **Option B is rejected on measured throughput**, not on taste: ~500 claims
   per second with JVM warmup is not an adjudication-path component. The jar
   stays, as the CI parity oracle and as an optional provider for a customer
   who asks for it.
3. **The parity target is now a known quantity**: >50,000 reference claims at
   100% match has been done by someone else against the same software. That is
   the bar for the parity corpus, and "we agree with CMS V44 on N claims" is the sentence
   diligence should get.

Build order:

1. `GrouperEngine` contracts, domain types and the grouping-result audit
   record (billed DRG vs assigned DRG vs paid DRG, with the reason).
2. Table import and persistence for the MS-DRG definition tables, weights and
   mean LOS, versioned by FY and effective date. Source the tables from the
   CMS jars and cross-check them against the Appendix files, not the reverse
   (see above). Include the ICD-10 forward-mapping tables, so an old stay can
   be re-grouped under a current version.
3. **Stand the CMS parity oracle up now, not last.** ✅ **Done**, in
   `tools/cms-grouper-parity-oracle/`. CMS's V44 grouper runs over a 34-stay
   invented corpus and its answers are committed, so the .NET test
   `CmsParityTests` holds CHO to them on every build without the licensed jars
   being present.

   This was step 7 until the jars were opened. The whole case for building
   rather than buying rests on being able to say we agree with CMS, so the
   harness that proves it should exist before the thing it judges, not after.
   It paid for itself on its first run, before a line of the real grouper was
   written, by correcting five things that had been written down as fact on a
   reading of the definitions manual:

   - **Two complication levels in the seed slice were wrong.** `J181` is an
     MCC, not a CC; `E1165` is neither. Both are now fixed, and one unit test
     that asserted the wrong level with it.
   - **An unrecognised secondary diagnosis or procedure is not an error.** The
     claim groups anyway and the code is reported as `NEITHER`. Only an
     unrecognised *principal* diagnosis returns `INVALID_PDX`.
   - **`J9600` is acceptable as a principal diagnosis** and groups to 189.
   - **The sex edit did not fire.** `N400` on a female stay groups to 726 with
     `grc=OK`, exactly as on a male stay. Until that is explained, CHO must not
     produce the `DemographicConflict` status it currently declares. Open
     question for step 4.
   - **A partial table set must refuse to group, not guess.** Four corpus rows
     had CHO reaching a confident *wrong* DRG, every one because its 40-code
     seed slice cannot tell "this code is genuinely not a complication" from
     "this code is not in my slice" — so it guesses, and guesses low. A wrong
     DRG is worse than no DRG. Step 4 must make unknown-code handling explicit
     rather than inheriting a default of "not a complication".

   The oracle also reports more than the DRG: the return code, HAC status and
   per-code severity usage (`CC`, `MCC`, `NEITHER`, `MCC_EXCLUDED`). Two
   groupers can agree on the DRG while disagreeing about which code earned the
   severity, and a harness comparing only the DRG calls that a pass and breaks
   on the next claim.
4. The MS-DRG grouper proper. **In progress.** The formula engine is done:
   `GrouperEngine/Formulas/` holds the lexer, the recursive-descent parser and
   the evaluator, and its operator precedence is checked against CMS's own
   parser by `FormulaPrecedenceCheck` in the oracle tool rather than against a
   reading of it.

   The language is six tokens — an operand name, two parentheses, and `&`, `|`,
   `~` — so the parser is small and now done.

   **Correction.** This ADR previously said that an operand evaluates as plain
   set membership and that "the remaining work is not an interpreter but a
   lookup". That was too rosy, and reading `MsdrgNodeSupplier` rather than only
   `ContainsNode` shows why. Set membership is the *default* case, but two
   things sit on top of it:

   - **Operands carry a position prefix.** `ANY:`, `PDX:`, `SDX:`, `ONLY:` and
     `PT:` say where the codes must appear — any diagnosis, the principal only,
     a secondary only, every diagnosis, or a patient attribute. 196 of the 452
     distinct operands in V44's 824 formulas are prefixed (`PDX` 170, `ANY` 14,
     `SDX` 11, `ONLY` 1). The same list appears under three different prefixes
     (`ONLY:incident`, `PDX:incident`, `SDX:incident`), so a grouper that
     ignored the prefix would collapse three different tests into one.

     CMS does not treat `:` as punctuation, so a prefixed operand is a single
     atom — the parser needed no change, and the split belongs where the
     operand is resolved. `OperandName` does it, and the tests confirm the
     lexer keeps `ANY:renalfail` whole.

   - **About a dozen operands are not lookups at all.** They are nodes with
     real logic: significant trauma, bilateral procedures, the fusion, vessel
     and stent *counting* nodes (`SINGLEFUSIONCTR`, `FOUR_VESSELS`,
     `FOUR_STENTS`, …), MDC-20 fall-through, only-incident, and — this is the
     useful part — the MCE edits, which are formula operands evaluated by a
     `GrcNode`: `INVALID_PDX`, `INVALID_SEX`, `INVALID_AGE`, `INVALID_DSTAT`,
     `PDX_ECODE`. Each appears in only a handful of formulas, so the volume is
     small, but they cannot be faked with a set.

   **This resolves the sex-edit open question from step 3.** `INVALID_SEX` is
   not a return code the grouper raises on its own; it is an operand in one V44
   formula, routed through a `GrcNode`. That is why `N400` on a female stay came
   back `grc=OK` — the edit lives in the formula logic and the gender tables
   (`codeGenderMdcs`), not in a demographic pre-check. So CHO's
   `GroupingStatus.DemographicConflict` is modelled in the wrong place and
   should come out of the formula evaluation instead. The mask builder now does
   exactly that.

   **How the default operand actually evaluates**, since the correction above
   could be read as casting doubt on it: it is mask membership, and the first
   description was right about that much. CMS's evaluation context
   (`logic/ast/AstContext`) carries the claim's codes, the MDC, a return code,
   an accumulator — and a **mask**, which is the precomputed set of attributes
   the stay satisfies. `logic/ast/ContainsNode` is exactly
   `mask.contains(attribute)`, where an attribute is the triple (prefix,
   negated, list name). So the model holds; the key is the triple rather than a
   bare name, which is what `OperandName` now carries.

   The one thing that model was missing is that CMS **accumulates**: a
   successful lookup records its attribute into the context before returning
   true. That accumulation is the audit trail, and it feeds severity
   suppression. CHO's evaluator now does the same, and matches CMS on the two
   details that follow from it, both pinned by tests:

   - **The record is partial, because evaluation short-circuits.** CMS's `And`
     and `Or` are Java `&&` and `||` — left first, right skipped once the
     answer is settled — so an operand to the right of a satisfied `|` never
     tests and is not part of why the DRG matched. CHO's operators
     short-circuit identically and in the same order.
   - **A negated operand that tested true is still recorded.** In `~a`, the
     lookup on `a` succeeded and accumulated before the negation flipped the
     result, so `a` is recorded even though the expression is false — it is the
     reason it is false.

   This is what makes a grouping decision explainable, which is the whole point
   of `MsDrgGroupingResult`: the DRG alone never says which diagnosis or
   procedure earned it, and that is what an appeal turns on.

   **The mask is now built**, in `GrouperEngine/Mask/`, as a port of CMS's own
   `MsdrgMaskBuilder` read from the V440 sources rather than inferred from
   behaviour. Three things in it were not what a reasonable design would have
   guessed, and each would have been a wrong DRG rather than an error:

   - **An unprefixed operand does not reach a diagnosis at all.** CMS attaches
     `PDX:` to the principal diagnosis's operands, `SDX:` to each secondary's,
     and no prefix to procedures. Its matching rule then has exactly one
     special case: `ANY:` on either side matches `PDX:` or `SDX:` on the other.
     So a bare operand in a formula is a procedure or claim-level test, which
     is why 196 of the 452 operands carry a prefix — the diagnosis lists cannot
     be reached without one.

   - **The mask cannot be a set.** Because `ANY:x` matches `PDX:x` and `ANY:x`
     matches `SDX:x` while `PDX:x` does not match `SDX:x`, the matching
     relation is not transitive, and a non-transitive relation cannot be
     hash-code equality. CMS reaches the same conclusion by construction: its
     mask is an `ArrayList` and every lookup is a linear scan. CHO's
     `AttributeMask` is the same, with a test that fails if someone
     "optimises" it into a `HashSet`.

   - **Several operands are claim-level and derived, not looked up.** `ORPROC`
     is computed per procedure from membership of `d477` or `d468`. `ALIVE` is
     the *absence* of a death, so an unreadable discharge status is alive and
     invalid, not dead. `XFRNBA` marks six specific statuses. `ONLY:incident`
     is the one operand meaning "all of them". And `INVALID_SEX` turns out to
     fire only when the sex is *unknown* against a sex-specific principal
     diagnosis — a stated sex the code does not allow is still a conflict, but
     CMS falls the MDC back to the code's female entry and groups the stay. A
     claim that contradicts itself and one that is merely incomplete are
     treated differently, and only the second is ungroupable.

   Because the tables are CMS table data and cannot be vendored, the engine
   depends on an `IMsDrgCodeAttributeSource` interface and the tables are
   imported at run time from a release the operator licences. Unknown codes are
   reported out of the build rather than swallowed, which is the mechanism the
   parity run said step 4 needed.

   What is left:

   - the runtime importer for `diagnoses`, `procedures`, `dxPatterns` and
     `prPatterns` behind that interface;
   - procedure **clusters**: CMS resolves combinations of codes into a
     synthetic procedure and suppresses the individual codes' operands in
     named MDCs, which the mask builder declares as its one remaining gap;
   - the dozen special-logic operand nodes, counting nodes included;
   - driving the ranked formula lists per MDC — 824 formulas for V44 — with
     `reRouteMdcId` (20 formulas) and `severitySuppressionOperand` (56).

   None of this changes the recommendation or the build order. It does mean
   step 4 is larger than this ADR implied two commits ago, and that the CMS
   extracted tables cannot simply be committed.
5. `IppsPaymentCalculator`: wage index, labor/non-labor split, IME, DSH,
   outlier, transfer and post-acute transfer policy; plus the
   provider-specific-file home the data model is missing.
6. `AddendumBApcAssigner` and OPPS packaging: status indicators, N packaging,
   Q1/Q2/Q3 conditional packaging, J1/J2 comprehensive APCs, composite APCs,
   multiple-procedure discounting on status T.
7. **The licensed-grouper adapter.** Listed as a build step rather than as a
   deployment note, because it is a commercial requirement and not optional
   architecture: every payer platform in this market integrates one, a
   prospect who already licences GPCS or EASYGroup will ask on the first call,
   and APR-DRG and EAPG cannot be had any other way. Shipping the adapter is
   what makes "or bring your own grouper" a true sentence.
8. Wire into `PricingStage` and `RepricingService` behind a per-plan toggle,
   so a plan can keep paying the billed DRG until it has been through parity
   testing.

### 6. Consequences

- Adjudication stops trusting the facility's DRG. That is the point, and it
  will change paid amounts, so the toggle in step 6 is not optional.
- The platform gains a reference-data obligation it does not have today:
  quarterly OPPS addenda and annual IPPS tables, on the NCCI loader pattern.
- APR-DRG and EAPG remain out of scope and licensed; the interfaces admit
  them.
- A Java sidecar becomes a supported but optional deployment shape, which the
  Kubernetes-first posture of ADR 005 can carry.

