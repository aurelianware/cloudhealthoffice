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
     `SDX` 11, `ONLY` 1). A grouper that ignored the prefix would collapse
     different tests into one. (The example this ADR used to give —
     `ONLY:incident`, `PDX:incident`, `SDX:incident` — turned out to be the one
     case where the prefix does *not* decide the answer. See the correction
     below.)

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

   **The runtime importer is in**, as `CmsJsonTableImporter`. It reads a
   release's `diagnoses`, `procedures`, `dxPatterns`, `prPatterns` and
   `codeGenderMdcs` for one version and refuses rather than degrades: a code
   with two rows for one version is the release contradicting itself, a
   dangling pattern number means the files are from different releases, and a
   version the release does not carry imports as nothing — which is the one
   outcome that must not look like success, since it makes every stay
   ungroupable for a reason nobody would look for in the table files. Verified
   against a real V44 release: 74,879 diagnosis codes and over 50,000
   procedure codes, behind a `CMS_MSDRG_TABLES` gate so CI never needs a
   release.

   That gate also now carries the check that should have existed from the
   start: **every complication level in CHO's seed tables is compared against
   the release's.** Those are the two levels the parity oracle corrected
   (`J181` recorded as a CC where CMS makes it an MCC, `E1165` as a CC where
   CMS makes it neither), and running CMS's whole grouper to find them was a
   roundabout way of asking a question the tables answer directly. They now
   agree on every code.

   **Procedure clusters are in**, which closes the mask builder's last
   declared gap. A cluster is a combination of procedure codes that qualifies a
   stay for something no single code does: it is satisfied when the claim
   carries a code from every one of its choice groups, and V44 has 4,084 of
   them over 594 distinct procedure codes, so most claims need no cluster work
   at all. Two parts of the mechanism are easy to get backwards and both are
   pinned by tests:

   - A satisfied cluster's own code is looked up in the **procedure** table
     like any other procedure, because that is what it now is to the rest of
     the grouper. Every V44 cluster code is a code that table knows, which the
     gated test checks over the whole release.
   - The contributing codes' operands then stop counting in the MDCs the
     cluster restricts, so the stay is credited for the combination and not
     also for its parts. The cluster's own operands are never suppressed —
     suppressing those would suppress the very thing the cluster exists to
     contribute.

   Suppression turned out to be a property of the **evaluation**, not of the
   operand: CMS builds two evaluators over the same formula text, one whose
   plain operands respect cluster suppression and one whose do not, and chooses
   between them by which path is grouping. So CHO's evaluator now takes a
   context — mask, MDC, and whether suppression applies — rather than a bare
   mask. Had that gone the other way round it would have been a wrong DRG on
   every stay with a cluster.

   **Second correction, and this one was found by asking CMS rather than
   reading it.** `SpecialNodeCheck` in the oracle tool drives CMS's real node
   supplier over chosen formulas and masks and prints its answers. Doing that
   found two defects in work this ADR had already called done:

   - **The MCE edit operands are spelled in the wrong case.** CMS's tables
     spell them `invalid_pdx`, `invalid_sex`, `pdx_ecode`, `invalid_dstat`;
     its *formulas* spell them `INVALID_PDX`, `INVALID_SEX`, `PDX_ECODE`,
     `INVALID_DSTAT`. The resolution is case-insensitive and then normalises to
     the tables' spelling, so the mask must hold the lower-case one. CHO's mask
     held the upper-case one for three of the four. Since attribute matching is
     case-sensitive, those three edits could never have fired, and a claim with
     an invalid principal diagnosis would have grouped as though it had a valid
     one. No error, just the wrong DRG. CMS confirms an upper-case mask entry
     answers false.

   - **A position prefix on a special list name is discarded.** The node
     supplier looks the name up without the prefix, so `PDX:incident` and
     `SDX:incident` are both evaluated as `ONLY:incident`. CMS answers true for
     `PDX:incident` against a mask holding only `ONLY:incident`, and false
     against one holding only `PDX:incident`. So the real V44 MDC 15 formula
     `(PDX:incident & SDX:mdc15fallthrough) | (PDX:mdc15fallthrough &
     SDX:incident) | ANY:drg794codes` does not test the two positions
     separately, however much it reads as though it does — and `incident` was
     exactly the example used above to argue that prefixes matter. It was the
     wrong example.

   Both are fixed, and the 22 answers CMS gave are now assertions rather than
   claims. The lesson is the one from step 3: where a question can be put to
   CMS's own grouper, reading its source is the slower and less reliable way to
   answer it.

   **The code-driven operands are now built, and asking CMS paid for itself a
   second time.** Eleven V44 operands read the claim's codes rather than its
   mask, and every one of them appears in a real V440 formula. `CodeDrivenNodeCheck`
   runs CMS's own nodes over synthetic codes, and four of the rules turned out
   not to be what the operand names say:

   - `MULTST` and `BILATERAL` take **at most one list per code**. CMS takes
     from each code the first list it carries that it has not already found and
     then moves on, so one diagnosis on both the head and the chest trauma
     lists is not a multiple-trauma stay. Two codes carrying one each are.
   - `BILATERAL` is satisfied by **any two** of its six lists, so a right hip
     and a right knee satisfy it. It never checks that the two are a left/right
     pair.
   - `MULTFUSE` wants two occurrences but **not** two different lists, where
     `SINGLEFUSIONCTR` wants both — and the two are built from the same fusion
     lists. The same list twice satisfies one and not the other.
   - The vessel and stent counts are **weighted**: `vessel3` is worth three, so
     one code can satisfy `FOUR_VESSELS` alone while three `vessel1` codes
     cannot.

   And a fifth trap, in CMS's naming rather than its logic: two of its
   node-factory members are not named after the operand they match.
   `SGLANTFUSECTR` matches the operand `SGLANTCTR`, and `SGLPOST1FUSECTR`
   matches `SGLPOST1CTR`. A table keyed on the member names — the obvious way
   to build one — leaves both counters as ordinary mask lookups, and a mask
   lookup on one of these names is always false. That is two of the eleven
   silently dead, with no error to notice.

   Two smaller points of faithfulness that the same run settled. A diagnosis
   list name is matched **case-insensitively and without its prefix**, because
   CMS's diagnosis lookup walks the list comparing names; a procedure's is
   matched **exactly**, because its lookup is a hash-set lookup. So a diagnosis
   carrying `STHEAD` satisfies `MULTST` and a procedure carrying `RIGHTHIP`
   does not satisfy `BILATERAL`. And a satisfied cluster's pseudo-code is
   **not** among the procedures these operands walk: a cluster reaches the
   formulas through the mask only.

   The guard that refused these operands is kept, now for a special list with
   no rule, so the next fiscal year's new list is loud rather than quietly
   false.

   **The ranked formula driver is built, and it is a loop rather than a
   decision tree**: an MDC's rows are tried in rank order and the first
   formula that matches wins. Two properties of that loop are quiet when got
   wrong:

   - **The mask is not constant.** Severity is recomputed for *every* row,
     with that row's own suppression list applied, and added to the mask as an
     operand before the row is evaluated. 56 of V44's 824 rows carry a
     suppression list. A grouper that computed severity once before the loop
     would pay those 56 at the wrong severity, and the other 768 — which write
     a suppression list of one empty string, matching nothing — would hide
     that nothing was wrong.
   - **Rank ties keep the table's own order**, because CMS's sort is stable.
     In several MDCs a row that assigns no DRG shares a rank with a real DRG
     row. That tie is harmless, and checkably so: the row cannot match at all.
     A gated test asserts that against a real release.

   Severity also needed the **CC exclusion lists**, which are the mechanism
   that stops a stay being paid twice for one clinical fact. V44 lists a group
   for 26,168 principal diagnoses pointing at 3,013 shared groups, the largest
   listing 5,565 codes. Three things take a secondary out of the severity
   count — the exclusion list, an `mccalive` code on a stay the patient did
   not survive, and a hospital-acquired condition — and all three are
   reductions, so leaving any of them out pays *more* than the claim has
   earned, with no error to notice. The first two are built; HAC determination
   is not, so the severity processor takes the set as a parameter and its
   contract says a caller who cannot supply it must refuse the claim.

   **MDC assignment is built, and it is not what this ADR said it was.** The
   previous revision of this section said the MDC comes from the `MDCnn` rows
   in the DRG logic table. It does not. Reading CMS's own chain settled it:
   `PdxMdcGrouping` is one line — `processingData.getPrincipalDx().getMdc()`
   — so a stay's MDC is read straight off its principal diagnosis's row in
   the code table, and no formula is searched for it. The `MDCnn` rows are
   vestigial: no V44 code carries an `MDCnn` operand, so they cannot match.

   That matters beyond the wording, because the wrong reading would have built
   a search where CMS has a lookup, and a search over rows that can never
   match answers "no MDC" on every claim.

   What CMS does instead is three passes over rankings, in this order, and the
   order is the whole of it:

   1. **Pre-MDC.** MDC 0's ranking runs on every stay. It holds the transplant
      DRGs, 7 MCE edit rows, and 3 rows that route a stay out to multiple
      trauma (24), HIV (25) or kidney transplant (11).
   2. **The principal diagnosis's MDC** — but *only* if the pre-MDC pass
      matched nothing. A pre-MDC match skips this pass even when it assigns no
      DRG.
   3. **Re-routing**, at most two hops. Not a loop: CMS looks at the reroute
      on the second hop's match and stops.

   Four details of that chain are each quiet when got wrong, and all four are
   asserted by tests:

   - An MCE edit can fire at **any** of the three passes, not just the
     pre-MDC one: V44 carries edit rows in MDC 0, 5, 15 and 20.
   - The **first** hop's MDC is recorded and the second's is not, and the MDC
     CMS reports is read off the end of that list — so a two-hop stay
     reports the first destination, not where its DRG came from. MDC 29 is a
     shared destination and reports the MDC the stay was routed out of.
   - A hop that matches nothing leaves the stay on a row that assigns no DRG,
     which ends grouping with no answer rather than keeping the pre-reroute
     DRG. CMS throws there and so does CHO.
   - The severity reported is the **last** ranking's, not the pre-MDC pass's.

   The 56 rows that assign no DRG now have a checked account of themselves: 7
   MCE edits, 20 reroutes, and 29 that cannot match at all. The last 29 are
   safe for a reason the table nowhere states — they name an operand nothing
   supplies — so a gated test re-derives that from each release rather than
   trusting it. Two of them are subtler than the `MDCnn` rows: MDC 5 rank 87 is
   `(ANY:ami) & (ami)`, dead only because a diagnosis's lists enter the mask
   with a position and the unprefixed half can never be satisfied, and MDC 15
   rank 2 is the single operand `5`.

   One more thing that run settled: `invalid_age`, one of CMS's five MCE edit
   operands, appears in **no** V44 formula, and CMS's own mask builder writes
   no age operand. So CHO's not writing one is correct rather than a hole.

   **Confidence.** This step is the least evidenced part of the grouper. The
   code-driven operands and the severity count were each built against answers
   printed by CMS's own classes; this chain was read off `GrouperChain`,
   `PreMdcGrouping`, `PdxMdcGrouping`, `MdcRerouteGrouping` and
   `MsdrgDrgResults` and asserted against hand-built tables. The end-to-end
   parity run is what will confirm the order.

   **Hospital-acquired conditions are built, and so are the two grouping
   passes around them.** A HAC is a condition Medicare will not pay the
   hospital for having caused, so it stops counting towards the stay's
   complication level — which can drop the stay out of a severity-split DRG
   into its lower-paying sibling. CMS therefore decides the DRG **twice** on
   every stay: once with every secondary counting, then the HAC policy, then
   again. Both answers are part of the result, because the pair is the payment
   mechanism and either one alone cannot explain the difference.

   V44 has 14 conditions, each with one formula, over 5,281 codes. The
   determination is per secondary diagnosis and reads the POA indicator: a
   condition present on admission (or clinically undetermined, `W`) is not one
   the hospital caused; a code the CC exclusion list already removed is not
   reconsidered; otherwise the condition's formula decides it. A HAC formula is
   evaluated outside any MDC, with no cluster suppression, over the secondary
   diagnoses only — not the principal.

   **Three behaviours here are CMS's rather than the policy's, and all three
   are replicated.** Parity is the requirement: firing a condition the payer
   does not takes severity off a stay the payer pays in full, which underpays
   the hospital.

   - **HAC 6 cannot fire in V44 at all.** A code gets its conditions only when
     one of its HAC list names starts with an upper-case `HAC` — CMS's check
     is `getListName().startsWith("HAC")`, and Java's is case-sensitive. Every
     V44 code on HAC 6 is on it through `hac06_show` or `hac06_sdx` alone, so
     catheter-associated urinary tract infection is never determined, its
     formula never evaluated, and its show-list rule never reached. Two of the
     nine `hac11_sdx` codes go the same way. A gated test re-derives this from
     each release, so a fiscal year that changes it fails loudly — and the
     show-list rule is built and tested against that day.
   - **The unknown-reporting-status check is a tautology.** CMS counts
     secondaries where `poa != Y || poa != W`, which is true of every value, so
     it counts every secondary on the claim. Two or more, on a stay with any
     met condition and a facility whose POA reporting status is unknown,
     refuses the claim. Named as such in the engine so it cannot be read as
     arithmetic that was meant.
   - **One unreadable POA anywhere refuses the claim**, not only on the code
     carrying the condition. CMS sets the flag while walking every HAC-bearing
     secondary and then applies it to every condition but HAC 6.

   `invalid_age` and the age edits are not involved: as above, V44 names
   neither.

   **Confidence.** Like MDC assignment, this is source-derived rather than
   oracle-confirmed, with one exception worth naming: the HAC 6 gate is
   confirmed against the release's own tables, not just against CMS's code.

   **The parity run is done, and the engine agrees with CMS on every row.** All
   33 rows of the oracle corpus that CMS grouped match on **five** things each
   — DRG, MDC, base DRG, severity and the medical/surgical partition — not on
   the DRG alone, because a right DRG reached through the wrong MDC is a
   coincidence waiting to stop happening. The 34th row, which CMS refuses, is
   asserted separately: it refuses with the same return code.

   The corpus rows exist to separate a correct grouper from a plausible wrong
   one: three-way severity splits, CC exclusions, the pre-MDC rules, the
   surgical hierarchy, the sex edit, and the present-on-admission cases where a
   hospital-acquired condition moves the stay to a lower-paying DRG. So the two
   source-derived chains above — MDC assignment and the HAC policy — now have
   evidence behind them and not only a careful reading.

   **The run found one defect, and it was in CHO's policy rather than its
   logic.** The engine refused any claim carrying a code outside the release's
   tables, on the reasoning that an unrecognised code is not an absent one.
   That is stricter than CMS and wrong in the direction that matters: CMS groups
   a stay with an unknown secondary diagnosis or procedure as though the code
   were absent, and refuses an unknown *principal* through its own
   `invalid_pdx` edit with a return code attached. Three corpus rows
   disagreed for that reason alone. The engine now groups all three the way CMS
   does and reports the unrecognised codes alongside the answer, so a caller can
   still reject the claim back to the biller — as its own policy, not as the
   grouper's answer.

   A second run, over a broad sample of the release's own codes rather than the
   corpus, grouped 1,684 of 1,876 stays and refused 192 with **nothing thrown**.
   That is not parity — nothing in it knows what CMS would have said — but it
   is the run that exercises every part of the chain at scale, and the failure it
   exists to catch is a chain that works on hand-built tables and falls over on
   CMS's own.

   What is left in this stage:

   - A larger corpus. 34 rows is enough to catch a wrong chain and not enough to
     claim parity in general; the oracle takes a corpus file, so growing it is
     work rather than design.
   - CMS's two marking passes, which record which codes affected the DRG. The
     matched operands carry most of the same information from the other
     direction.

   None of this changes the recommendation or the build order. It does mean
   step 4 is larger than this ADR first implied, that one thing this ADR
   asserted about MDC assignment was wrong, and that the CMS extracted tables
   cannot simply be committed.
5. `IppsPaymentCalculator`: wage index, labor/non-labor split, IME, DSH,
   outlier, transfer and post-acute transfer policy; plus the
   provider-specific-file home the data model is missing.

   **Built, and now validated against CMS's own pricer.** Every formula is
   taken from
   42 CFR with the paragraph cited at the line that implements it — 412.64(h)
   and (m) for the wage adjustment, 412.105 for indirect medical education,
   412.106 for disproportionate share, 412.4 for transfers, 412.84 for cost
   outliers. That is a weaker claim than the grouper's, and the difference
   matters: the grouper is trustworthy because it was run against CMS's own
   grouper and agreed on every case, not because it was written carefully.
   Reading a formula correctly and implementing it the way CMS's PRICER does
   are different things, and only the second gets a payment right. So
   `IppsPayment` keeps every intermediate — the wage-adjusted amount, which
   labor share was chosen, each add-on, the per diem, the threshold — so that
   a PRICER parity run can compare step by step. A single total that agrees is
   indistinguishable from one that agrees by luck.

   **No rate is in the code.** A fiscal year is an `IppsRateSet` loaded like a
   grouper version: the eight-row operating rate table, both labor percentage
   pairs, the capital rate, the fixed loss amount, the burn DRGs, the teaching
   exponent and multiplier. CMS republishes all of it every August for the October
   discharges, and an engine with a rate compiled into it is wrong from
   1 October until someone ships a release.

   **The provider-specific file is input, not computation**, and two of its
   fields look computable and are not. The disproportionate patient percentage
   of 412.106(b) is built from Medicare/SSI and Medicaid patient days across a
   cost reporting period; the resident-to-bed ratio of 412.105(a) from
   full-time-equivalent residents over average available beds with several
   categories of bed day excluded. Both are settled on the cost report, under
   caps and category rules, with the hospital's appeal rights attached. A
   claim-time engine that derived either would be inventing the hospital's
   payment rather than applying it.

   **The parity run exists, and it reversed one of the three findings this
   step shipped with.** CMS retired the downloadable PC Pricer in favour of a
   browser tool, which would have made a poor oracle — but it still publishes
   the pricer itself as a runnable jar, to satisfy Freedom of Information Act
   requests, along with its Java source *with the original COBOL quoted as
   comments above each rule*. `tools/cms-ipps-pricer-oracle` runs it over
   thirty-two invented stays and commits what it paid;
   `IppsPricerParityTests` holds the calculator to those amounts on every
   build, with no CMS artifact present. The tool's README has the whole of it,
   including the five undocumented provider-record fields that stop a
   well-formed request before any arithmetic happens.

   The calculator now agrees with CMS on the base DRG payment, the labor split,
   every transfer fraction, and the capital outlier exactly, on every stay whose
   inputs it can express. What the run corrected:

   - **The labor share is a threshold after all, not a comparison — and this
     ADR previously asserted the opposite.** 412.64(h)(3) sets 62 percent
     "unless employing that percentage would result in lower payments for the
     hospital", which is written as a comparison, and that is how this was
     implemented. The two readings agree wherever the cost-of-living adjustment
     is 1.0 and come apart in Alaska and Hawaii, where 412.64(m)(1)(ii) lifts
     the non-labor share. CMS's `CalculateNationalPercentages` takes the larger
     share and then replaces it with the statutory one
     `IF (H-WAGE-INDEX < 01.0000 OR H-WAGE-INDEX = 01.0000)` — the wage index
     alone, with the cost-of-living adjustment playing no part in the choice.
     On the corpus's Alaska stay CMS pays 15,368.43 where the comparison
     reading pays 15,424, so CHO was *over*paying those two states on the
     strength of a correct reading of the regulation. The lesson generalises:
     where the regulation and CMS's program differ, the program is what gets
     paid.
   - **The fixed loss amount is apportioned per side, not applied whole and not
     combined.** Three attempts. The first tested operating and capital each
     against the whole amount, which counts it twice and pays outliers on
     ordinary cases. The correction added the two costs and tested them against
     one threshold. CMS does neither: it splits the one amount between the
     sides *in proportion to the hospital's two cost-to-charge ratios*, so the
     shares sum to one, and adjusts each side by its own geographic factor —
     the labor split and wage index for operating, the capital geographic
     adjustment factor for capital. Those differ for almost every hospital.
   - **A same-day transfer is paid two per diems, not one.** 412.4(f)(1) pays
     twice the per diem for the first day and one for each later day, which is
     the length of stay plus one — but CMS floors the length of stay at one day
     *before* adding it. CHO paid a zero-day transfer half what CMS pays it.
     The same rule has a consequence worth stating: because every stay is paid
     at least two per diems, **no transfer reduction is possible at all in a
     DRG whose geometric mean is under two days**, which is why the corpus's
     transfer rows had to be moved off DRG 470.
   - **The ambiguous transfer line is settled, and the guess was right.**
     412.4(f)(2) pays "50 percent of the amount calculated under (f)(1) for
     each day of the stay", which admits a reading where the halving is per
     day. CMS computes `0.5 + ((0.5 × per diem days) / mean)`, the
     whole-amount reading, which is what was implemented.
   - **Which post-acute rule a DRG carries is DRG table data.** It arrived as a
     boolean the caller asserted, which is the same misplacement as deriving a
     hospital's disproportionate patient percentage from a claim. It is now an
     `IppsPostAcuteTransferRule` read beside the weight and the geometric mean,
     and CMS's own pricer takes it from two columns of its DRG table and
     ignores what the claim says.

   **The standardized amount is an eight-row table, and that closed two gaps
   at once.** The first run found the hospital quality indicator moving the
   standardized amount itself, which one amount and a pair of shares could not
   express. Reading CMS's source for how showed a bigger shape:
   `2050-RATES-TB` is a straight eight-way branch on three facts — whether the
   hospital met the inpatient quality reporting requirement, whether it carries
   the electronic-health-record reduction, and whether its wage index is above
   1.0 — and the eight rows are four standardized amounts, one per combination
   of the two reductions, each split two ways by the wage-index rule. So the
   quality gap and the record-reduction gap were one gap, and the fix was to
   replace the single amount with CMS's table rather than to bolt two flags on.

   Two things the table settles that a reduction factor would have got wrong.
   The reductions are **independent**, so a hospital can carry both and is paid
   a fourth published amount rather than two reductions applied in turn. And
   the rows hold **dollars, not a percentage of one amount**: the
   record-reduced row above a wage index of 1.0 is 4,349.21 labor and 2,240.51
   non-labor, which sum to 6,589.72 exactly, while 6,589.72 × 0.66 is
   4,349.2152 and × 0.34 is 2,240.5048 — CMS rounds the two independently and
   deriving either from a share moves the answer by a cent on every claim in
   that row.

   The labor percentages stayed, with one job. CMS's outlier threshold is the
   year's cost threshold times the percentages, wage- and
   cost-of-living-adjusted, and the standardized amount plays no part in it.
   They are the only thing the 62-versus-66 rule now decides.

   The pricer exposes no endpoint for its rate table, so the only way to learn
   a row is to price a claim that selects it. Five corpus stays were added for
   that, and `IppsPricerParityTests` now builds the whole eight-row table out
   of CMS's answers and asserts CHO paid from the same row — which exercises
   CMS's selection branch as well as its amounts, where reading the file would
   not have.

   **The four hospital-level adjustments are built, and the order is the whole
   content of them.** The readmissions reduction, the value-based purchasing
   adjustment, the low-volume adjustment and the hospital-acquired-condition
   penalty are taken on the assembled payment rather than on the DRG amount,
   in an order that is not derivable from 42 CFR and that changes amounts. From
   `CalculateHospitalAcquiredConditionReduction`, which despite its name
   assembles the total, and `CalculateLowVolumeTotals`:

   - The **low-volume uplift** is a percentage of everything through the
     new-technology add-on — both sides, the cost outlier and the
     uncompensated care payment included — and **not** of the readmissions or
     value-based adjustments. On the corpus's low-volume stay that is 0.20 ×
     83,208.50, and a calculator taking it on the DRG amount would pay 2,560
     instead of 16,641.70.
   - **Readmissions and value-based purchasing** are each `(factor − 1)` times
     the base DRG payment *alone*, carried as signed amounts in the sum. Not
     the whole operating side: the teaching, disproportionate share and
     uncompensated care payments are all outside them.
   - The **penalty** is one percent of all of that together, so it reaches the
     outlier and the uncompensated care payment and is itself reduced by the two
     adjustments above it. On the corpus's all-four stay that is
     −0.01 × 103,882.64 = −1,038.83, against −128 for one percent of the DRG
     amount.
   - Pass-throughs, the bundled adjustment and the islet isolation add-on are
     added afterwards and **escape both**.

   Each of the four is compared against CMS separately rather than through a
   total, because a wrong order agrees on each one in isolation; the corpus has
   a stay carrying all four at once for the same reason. Two validation rules
   are copied as written, because both are provider-file facts and guessing
   either invents the hospital's payment: a factor outside CMS's range (0.97 to
   1.0 for readmissions, 0.98 to 2.0 for value-based purchasing) raises rather
   than being clamped, as CMS refuses the claim; and the low-volume adjustment
   needs the year's approval *and* a factor of at most 0.25, where a larger
   factor pays nothing rather than being capped.

   **The hospital-specific portion is built, and it is a comparison rather
   than an add-on.** Sole-community (42 CFR 412.92), essential-access and
   Medicare-dependent (412.108) hospitals are paid the greater of the federal
   rate and their own historical rate, and CMS pays the *difference* — so the
   portion is zero at any such hospital whose own rate has fallen behind, which
   is most of them most years, and an implementation that added the two would
   roughly double the payment. A Medicare-dependent hospital is paid 75 percent
   of the difference and the other two all of it, which is the only thing
   separating the three categories. Four things came out of reading
   `CalculateAdditionalHospitalSpecificPortion` that would not have come out of
   reading the regulation:

   - **The comparison is one-sided on a transfer.** The federal side carries
     the transfer fraction; the hospital's own rate carries the **full** DRG
     weight. A short transfer at one of these hospitals therefore lands on the
     hospital-specific side almost whatever the facility rate is. That is in
     CMS's program and not in the regulation, and the corpus's sole-community
     transfer is the row that pins it.
   - **The operating cost outlier is inside the comparison and the capital
     outlier is not**, and the uncompensated care payment is inside it too. On
     the corpus's tuned row CMS pays a 3,984.58 portion without the operating
     outlier and 2,294.26 with it — the 1,690.32 operating outlier taken off
     dollar for dollar — while the 5,394.00 capital outlier does not move it at
     all.
   - **The portion sits above the low-volume base and the penalty base**, so it
     has to be settled before the four adjustments rather than added at the
     end. The same row pays a low-volume uplift of 0.2 × a base the portion is
     inside, and a penalty of one percent of that total. It is *outside* the
     readmissions and value-based base, which stays the federal specific
     portion alone.
   - **The update factor's cumulative prior-year chain restarts in FY2026.**
     Through FY2025 CMS's pricer multiplied every market basket update back to
     FY2019 into it; `Ipps2026PricerContext` does not override it at all, so it
     is 1.0, and FY2027 then carries FY2026's update alone. A year loaded with
     the previous year's chain overpays every sole-community discharge by
     several percent and does it quietly, because the payment still looks like
     a payment. The factor is four published market basket updates — keyed by
     the same two payment-reduction flags as the rate table, and *not* by the
     wage index, because the hospital-specific portion is the hospital's own
     rate and nothing geographic touches it — times the budget-neutrality
     factors times that chain, rounded to five decimal places before anything
     is multiplied by it.

   CMS reports no column for that factor, and unlike a rate table row it cannot
   be read off a payment in general, because the portion it produces is a
   difference. On a plain discharge with nothing else on the federal side it is
   invertible exactly, so the corpus's two sole-community discharges are
   calibration rather than comparison — the standing the capital rate already
   has — and the parity test says so at the inversion. The other five
   hospital-specific rows are not implied by them.

   **Pass-through costs are paid, and they are the one amount the oracle
   cannot confirm.** `CalculateHmoTotals` is the only rule in the whole IPPS set
   that reads the provider record's pass-through fields, and it is gated on
   `isWebPricerRequest()` — the header `cms-pricer-client: WEB-PRICER`. Sending
   that header to the price-claim endpoint changes nothing, because the
   web-pricer path is not reachable on the published jar. So the formula is read
   off CMS's source with the COBOL quoted above it, the arithmetic is three
   lines, and it has not been *executed* by CMS. That is a weaker claim than the
   rest of this step makes, which is why it is stated at
   `IppsPayment.PassThroughPayment`, in the oracle's README, and here, rather
   than only in a commit message. Two things came out of the source anyway:

   - **It is per covered day, not per discharge:** `passThroughTotalAmount ×
     length of stay`. The provider-record field is named as a total and is a per
     diem, so reading the name underpays a 30-day stay thirtyfold. A transfer
     does not reduce it either — it is already a per diem.
   - **The organ-acquisition, direct-medical-education and stem-cell amounts are
     components inside that figure, not additions to it.** CMS subtracts them
     back out on a managed-care claim and otherwise never reads them; summing
     all four pays the same costs twice. The managed-care variant, where CMS
     also pays no teaching adjustment at all, is not modelled, because nothing
     in CHO distinguishes a managed-care claim yet.

   A corpus row carries a pass-through amount on file so the boundary stays
   checked: the column is empty on all thirty-two rows, a parity test asserts
   that, and the day CMS starts reporting a figure the test fails and the
   amount becomes comparable like everything else.

   **The Model 1 bundled adjustment was on this list and should not have been:
   it is dead code.** FY2026 uses `CalculateBundleReduction2021`, whose COBOL
   reads `IF B-DISCHARGE-DATE > 20161231 COMPUTE WK-MODEL1-BUNDLE-DISPRCNT = (1
   - (P-MODEL1-BUNDLE-DISPRCNT * 0))` — the discount is multiplied by **zero**,
   so the adjustment is `baseDrgPay − baseDrgPay` for every FY2026 discharge.
   Model 1 bundling ended on 31 December 2016 and CMS kept the path with the
   factor neutered. Confirmed rather than assumed: the same corpus row carries a
   10 percent discount and demonstration code 61, and CMS pays it the same total
   as the plain stay, with a parity test on that equality. Modelling it would
   have been modelling dead code, which is worth saying because it was written
   down as a gap for two revisions of this ADR.

   **What is still missing**, and why this is not yet wired into adjudication.
   CMS pays new-technology add-ons and the islet isolation add-on — a flat
   18,848 for one qualifying procedure code and 37,696 for more than one — and
   this pays neither. Both need CMS's code tables rather than a formula. And CMS works in
   covered days and lifetime reserve days, reporting regular, reserve and
   outlier days used, where `IppsStay` has no beneficiary-day concept at all —
   the last of those is a model change rather than an arithmetic one, and
   return code 67, "outlier LOS greater than covered days", is the pricer
   refusing a claim on day arithmetic.

   Deliberately absent, because approximating it would be worse than omitting
   it: new technology add-on payments, which are a published list of devices
   and amounts and so need an importer rather than a formula.
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

   **Done for `RepricingService`.** Taken out of order, and deliberately:
   nothing in the platform referenced the grouper engine at all, and
   `RepricingService` still carried the line "Cloud Health Office has no MS-DRG
   grouper" with an inpatient claim refused beneath it. A capability nothing
   calls is not a capability.

   Three things were built, and the first was not in the plan:

   - **`FormulaMsDrgGrouper`.** The seam `IMsDrgGrouper` already existed, and
     the implementation behind it was `TableDrivenMsDrgGrouper` — the earlier,
     simplified grouper. The engine that agrees with CMS on every corpus row,
     `MsDrgFormulaGrouper`, was a static class outside the seam, so a caller
     wiring the seam got the weaker grouper. That is now fixed and the
     parity-tested engine is what the seam resolves to.
   - **`BilledDrgGrouper`.** Reports the DRG the facility billed and groups
     nothing, which is what the platform did before. It is the registered
     default, so installing the seam changed no price anywhere. It is also the
     honest implementation for a payer that has contracted to accept the
     facility's grouping.
   - **`InpatientStayRequest`** on the repricing API. A repricing request
     carried diagnoses but no ICD-10-PCS procedures, no discharge date and no
     present-on-admission indicators, and grouping a stay without its
     operating-room procedures is not a partial answer but a wrong one: the
     stay lands in the medical partition of its MDC and returns a plausible
     DRG that pays the wrong amount. So a grouper is asked only when the
     request carries this block, and a claim that does not is refused in the
     words it was refused in before.

   Two details that cost more than they look:

   - **The DRG has to be resolved before the fee schedule is loaded.** Loading
     an MS-DRG schedule means fetching that DRG's rate row, so a grouping done
     after the load assigns a DRG the loaded schedule holds no rate for and the
     claim prices at nothing. The ordering is now the other way round and a
     test guards it.
   - **CMS's `drgSeverity` field is not the family's severity marker**, so the
     adapter leaves `MsDrgGroupingResult.Severity` unset. In V44, 731 of the
     824 rows carry 0, including every member of three-member families such as
     11/12/13 — a stay paid DRG 13 ("without CC/MCC") sits on a row marked 0
     exactly like the "with MCC" sibling above it. The split member could be
     inferred from position within the base family, but only safely for
     three-member families: in a two-member family the lower DRG may be "with
     CC/MCC" or "with MCC" and the table does not say which.
     `ComplicationLevel`, which the engine computes outright, carries the
     severity information instead.

   `PricingStage` is still to do, and it is the half that changes adjudicated
   payments, so it keeps the per-plan toggle this step was written around.

### 6. Consequences

- Adjudication stops trusting the facility's DRG. That is the point, and it
  will change paid amounts, so the toggle in step 6 is not optional.
- The platform gains a reference-data obligation it does not have today:
  quarterly OPPS addenda and annual IPPS tables, on the NCCI loader pattern.
- APR-DRG and EAPG remain out of scope and licensed; the interfaces admit
  them.
- A Java sidecar becomes a supported but optional deployment shape, which the
  Kubernetes-first posture of ADR 005 can carry.

