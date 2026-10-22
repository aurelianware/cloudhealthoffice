# CMS MS-DRG parity oracle

Runs CMS's own MS-DRG grouper over a reference corpus and writes down what it
answered, so CHO's grouper can be held to the same answers.

This is ADR 024 step 3. It comes before the grouper itself is written, not
after, because a grouper with no oracle is a grouper nobody can trust: the only
way to know whether a reimplementation of MS-DRG is right is to ask the real
one.

## What was in the way, and what turned out not to be

CMS ships the grouper as Java jars that compile against the Solventum Grouper
Foundation Classes (`com.mmm.his.cer.gfc.gfc-api:gfc-base-api`). CMS does not
redistribute that artifact and Solventum does not publish it to Maven Central —
a search of Central for `gfc-base-api` returns nothing. The source is public, at
`github.com/solventum-oss/GFC-Grouper-Foundation-Classes`, so building it was
always possible in principle.

It turned out not to be necessary. The CMS jars touch exactly five types from
that library:

| Type | What the jars actually use |
| --- | --- |
| `GfcPoa` | the constants `Y N U W E ONE BLANK INVALID`, and `toString()` |
| `ComponentRuntime` | the no-arg constructor, then `Map.get` and `Map.containsKey` |
| `Claim` | the no-arg constructor |
| `FoundationException` | `(String)` and `(String, Throwable)` |
| `FoundationRuntimeException` | `(String)` |

plus `Processable`, `ComponentName`, `ComponentPackage`, `ComponentVersion` and
`IClaim`, which appear only in signatures. That list is not a guess: it is the
set of `com/mmm/his/cer` entries in the jars' bytecode constant pools, read with
`javap -c` over all 1,601 classes. `foundation-shim/` implements exactly that
much, and `package-info.java` there records the descriptors it was derived from.

A shim is only acceptable here because it decides nothing. The jars contain no
`Class.forName` anywhere, so no code path is chosen by this code;
`ComponentRuntime` is read only through `Map` methods, so a `HashMap` subclass
reproduces it entirely; and `GfcPoa.toString()` is called from one place,
`TraceUtility`, which writes trace text and never reaches grouping. Every
grouping decision — MDC assignment, pre-MDC rules, the surgical hierarchy,
CC/MCC and CC exclusions, the severity split, the HAC processor — stays in CMS's
compiled classes, driven by CMS's own table buffer.

## Getting the inputs

The CMS jars are **not** in this repository and cannot be. CMS distributes the
MS-DRG software per requester, and the package carries American Hospital
Association UB-04 copyright. Download the "Java Standalone" MS-DRG software for
the fiscal year you need from the CMS MS-DRG Classifications and Software page,
and put these in one directory:

```
msdrg-core-44.0.0.3.jar          from the CMS package
msdrg-model-v2-2.12.2.jar        from the CMS package
msdrg-binary-access-1.6.4.jar    from the CMS package
protobuf-java.jar                protobuf-java 3.25.5, reads the table buffer
slf4j-api.jar                    slf4j-api 1.7.36, satisfies the jars' logging
```

`V44` is FY 2027. The version is pinned in `ParityOracle.VERSION` and in the
`gov.agency.msdrg.v440` package it imports, so a different fiscal year needs a
one-line change and a re-run.

## Running it

```bash
CMS_MSDRG_JARS=/path/to/jars ./run.sh
```

That compiles the shim, compiles the oracle, runs it over
`corpus/reference-claims.psv` and writes `corpus/expected-v44.psv`. Both files
are pipe-separated so they stay readable in a diff, and neither needs a JSON
parser — the CMS jars pin enough of the classpath already.

`corpus/expected-v44.psv` is committed. The .NET test
`CloudHealthOffice.GrouperEngine.Tests/CmsParityTests` reads it and compares
CHO's grouper against it, which is how the comparison runs on every build
without the CMS jars being present.

## The corpus

34 invented stays. There is no member, no provider, no claim number and no date
of service in the file — only code combinations and the demographics the grouper
needs. Rows are chosen to distinguish a correct grouper from a plausible wrong
one, and each carries a `why` column saying what it pins.

The output columns go beyond the DRG on purpose:

| Column | Why it is there |
| --- | --- |
| `drg`, `mdc`, `base_drg`, `severity`, `med_surg` | the answer |
| `grc` | the return code, so errors are compared as carefully as successes |
| `hac_status`, `hac_categories` | whether hospital-acquired-condition policy changed the DRG |
| `pdx_recognized` | whether the principal diagnosis was in the code set at all |
| `sdx_usage` | per-code: `CC`, `MCC`, `NEITHER`, `MCC_EXCLUDED`, … |

`sdx_usage` is the column that makes a failure diagnosable. Two groupers can
agree on the DRG while disagreeing about which code earned the severity, and a
harness comparing only the DRG calls that a pass and then breaks on the next
claim.

## What the first run already corrected

Running it changed three things that had been written down as fact on the
strength of the definitions manual alone:

- **An unrecognised secondary diagnosis or procedure is not an error.** The
  claim groups anyway and the code is reported as `NEITHER`. Only an
  unrecognised *principal* diagnosis returns `INVALID_PDX`.
- **J9600 is acceptable as a principal diagnosis** and groups to 189. The
  corpus row that was written to test rejection now records acceptance.
- **The sex edit did not fire.** `N400` on a female stay groups to 726 with
  `grc=OK`, exactly as it does on a male stay. Until that is explained, CHO
  must not produce a `DemographicConflict` the reference grouper does not.

That is the oracle doing its job before a single line of the real grouper was
written, which is the argument for building it first.

## Licensing

Nothing from the CMS package is committed here. This directory holds only
CHO-authored code: the compatibility shim, the oracle, the corpus, and the
grouper's answers for those 34 invented stays. The MS-DRG definitions, the code
attribute tables and the grouper logic stay in CMS's distribution, and this tool
is useless without it.
