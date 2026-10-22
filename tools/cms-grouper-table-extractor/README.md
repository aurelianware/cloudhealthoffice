# CMS MS-DRG table extractor

Pulls the CMS MS-DRG grouper's own tables out of the CMS Java release, as the
source of truth for `CloudHealthOffice.GrouperEngine` (ADR 024 §5 step 2).

## Why this exists

The grouper's tables are not really the definitions-manual appendices. The
appendices are a rendering for humans; what CMS *runs* is a protobuf buffer,
`runtime_buffers/msdrg_combined.bin`, inside `msdrg-binary-access-<v>.jar`.
Transcribing the appendix text would be reading the output of the data instead
of the data.

Reading that buffer needs **two jars**: the release's `msdrg-binary-access`
and `protobuf-java`. It does not need the grouper itself, and it does not need
`com.mmm.his.cer:foundation` — the 3M library the grouper's own runtime
options depend on, which is not published to Maven Central. That is why this
tool reads the buffer rather than calling the grouper.

## Getting the inputs

1. From the [CMS MS-DRG Classifications and Software page](https://www.cms.gov/medicare/payment/prospective-payment-systems/acute-inpatient-pps/ms-drg-classifications-and-software),
   download the version's **Java jars** package (e.g.
   `ms-drg-mce-v44-java-jars.zip`) and unzip it; it contains
   `MSDRG.V<version>.Java.Standalone.Jars.P.zip`, which contains
   `msdrg-binary-access-<v>.jar`.
2. `protobuf-java` from Maven Central (3.25.x is known to work).

## Running it

```sh
CP=msdrg-binary-access-1.6.4.jar:protobuf-java-3.25.5.jar
javac -cp "$CP" -d classes src/main/java/gov/cho/grouper/ExtractTables.java
java -Xmx5g -cp "classes:$CP" gov.cho.grouper.ExtractTables ./tables-v44
```

The buffer is read off the classpath, so the version extracted is whichever
`msdrg-binary-access` jar you put on it. `source-metadata.txt` is copied into
the output directory so an extracted set says which CMS build produced it.

## What comes out

One JSON file per table, protobuf field names and nesting preserved, no
reshaping. From V44 (about 30 MB in total):

| File | Rows | Holds |
| --- | --- | --- |
| `diagnoses.json` | 75,005 codes | Per code: MDC, severity (`MCC`/`CC`/`NCC`), and pattern numbers pointing at its operand and category lists |
| `procedures.json` | 83,558 codes | Per code: its operand pattern number |
| `formulaRoot.json` | 28 MDCs | The grouping logic itself (see below) |
| `clusters.json` | 4,111 clusters | Procedure clusters — combinations that count as one thing |
| `exclusionGroups.json`, `pdxExclusionGroups.json` | | The CC exclusion lists |
| `hacInformation.json`, `hacFormulaLists.json`, `hacDescription.json` | | Hospital-acquired-condition categories and their logic |
| `dxPatterns.json`, `prPatterns.json` | | The operand-name lists the pattern numbers index into |
| `drgDescriptions.json`, `baseDrgDescriptions.json`, `mdcDescription.json` | | Titles |
| `codeGenderMdcs.json` | | The sex-specific MDC assignments |
| `codeToClusterIds.json` | | Code to cluster membership |

## The thing worth knowing before you build against it

**CMS's grouping logic is boolean formulas, not rule rows.** Each MDC holds a
ranked list like this (MDC 8, abridged):

```json
{ "drg": 525, "baseDrg": 523, "mdc": 8, "surgical": "SURGICAL",
  "reRouteMdcId": 0, "formula": "fuse9plus | cmadifd | tulip",
  "severitySuppressionOperand": [""], "drgSeverity": 0,
  "versionRanks": [ { "rank": 3, "version": "V440" } ] }

{ "drg": 519, "baseDrg": 518, "mdc": 8, "surgical": "SURGICAL",
  "formula": "(CC & backneck)", "drgSeverity": 2,
  "versionRanks": [ { "rank": 74, "version": "V440" },
                    { "rank": 47, "version": "V431" } ] }
```

The operands (`fuse9plus`, `backneck`, `mvr`, `vascular`) are **attributes
attached to codes**, which is what `diagnoses.json` and `procedures.json`
supply through their pattern numbers; `CC` and `MCC` are the severity the
stay resolved to; `ANY:` scopes an operand across the whole claim. The
grouper's own release ships a lexer and parser for this expression language
(the `utility/farser` package in `msdrg-core`), which is the clearest possible
statement that the logic is data.

So an implementation that models the logic as rule rows with code sets — as
the first cut of `MsDrgTableSet` does — is a simplification of CMS's shape,
and will not reach parity. The faithful model is: code → attributes, plus a
ranked formula list per MDC, plus an evaluator for the expression language.
The surgical hierarchy falls out of `rank`; MDC re-routing out of
`reRouteMdcId`; CC exclusions out of `severitySuppressionOperand` and the
exclusion groups.

**One extract covers nine versions.** Every row carries the versions it
applies to (`V400` through `V440`), and `versionRanks` gives each version its
own hierarchy position. Grouping a stay by its discharge date is therefore
satisfiable from a single extracted set, which is what the engine's
`MsDrgTableSet.EffectiveFrom`/`EffectiveThrough` window needs.

## Licensing

CMS publishes this software free of charge and states that it provides no
technical support for incorporating it into systems outside Original Medicare
claims processing. The extracted tables are US federal government data. The
release's `OpenSourceLicenseDisclosures.pdf` covers its own dependencies.
Nothing here is redistributed in this repository: the tool reads a download
you fetch yourself.
