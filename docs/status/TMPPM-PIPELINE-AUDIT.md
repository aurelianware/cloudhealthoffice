# TMPPM Manual-Digitalization Pipeline — Audit Findings

**Scope:** `tools/CloudHealthOffice.TmppmIngestionService/` (the only manual-digitalization pipeline in the repo). Read-only audit; nothing modified, nothing run.
**As-of:** commit `cd2a3b0` (HEAD), audit date 2026-08-06.

---

## 1. Locate the pipeline

**Module:** `tools/CloudHealthOffice.TmppmIngestionService/` — a standalone .NET 8 console app (`CHO.TmppmIngestionService`). Notably **not included in `cloudhealthoffice-main.sln`** — it builds/runs on its own, outside the main solution.

**Entry point:** `Program.cs:23` (`Main`). CLI with three commands (`Program.cs:47-53`):
- `ingest <year> <month> [--tenant <id>]` → `IngestionPipeline.RunAsync` (`Program.cs:75-76`)
- `parse-section <chapter_id> <section_ref>` → single-section debug via C# parser (`Program.cs:104-116`)
- `download <year> <month>` → PDFs only (`Program.cs:149-151`)

**Public interface — `IngestionPipeline.RunAsync(year, month, tenantId?)`** (`Services/IngestionPipeline.cs:25`):
- **Input:** an edition (year+month) → downloads 8 hardcoded PDFs from TMHP; optional tenant id.
- **Returns:** `IngestionResult { EditionId, ChaptersDownloaded, ChaptersChanged, RulesExtracted, ConceptMapOverridesPublished }` (`IngestionPipeline.cs:148-155`).
- **Side effects:** writes to MongoDB/Cosmos collections `tmppm_pa_rules`, `tmppm_editions`, `concept_map_entries` (`TmppmRuleStore.cs:13-23`). No output is written to the repo/filesystem as committed artifacts.

**Wired vs. stubbed/half-finished — this is the headline finding:**

| Component | README claims | Reality in code |
|---|---|---|
| `TmhpChapterDownloader` | present | ✅ wired (`Loaders/TmhpChapterDownloader.cs`) |
| `TmppmPdfParser` (C# / PdfPig, regex) | "backup" | ✅ **this is the ONLY parser actually wired** into `IngestionPipeline` (`IngestionPipeline.cs:60`, `:111-117`) |
| `extract_section.py` (PyMuPDF) | "primary" extractor | ⚠️ **orphaned** — never invoked from any C# (no `Process.Start`/`python3` call anywhere in the tool). Standalone debug script only. So the "primary" extractor is not in the pipeline; the "backup" is. |
| `LlmAssistedParser.cs` (Claude API) | listed in diagram + file tree | ❌ **DOES NOT EXIST.** Only referenced in `README.md`. No such file. |
| `HybridParser.cs` (regex-first, LLM-fallback) | listed in file tree, "hybrid regex + LLM strategy" | ❌ **DOES NOT EXIST.** Only referenced in `README.md`. |
| Anthropic SDK / API key config | `.csproj` dep + `appsettings.json` "Anthropic" block | ❌ **No Anthropic package** in `TmppmIngestionService.csproj`; **no `Anthropic` section** in the actual `Config/appsettings.json`. The README shows both; neither exists. |
| Diff report (`Step 4`) | "generates a diff report" | ⚠️ **TODO stub** — `IngestionPipeline.cs:71-74` is a comment: `// TODO: Load previous rules and diff against new rules`. No diff is computed. |

Other flags:
- `TmppmRuleStore.SaveDiffReportAsync` (`TmppmRuleStore.cs:138`) and the entire `TmppmDiffReport`/`TmppmRuleDelta` model (`TmppmModels.cs:72-92`) exist but are **never called** anywhere — dead code.
- `Program.cs` usage text (`:17`, `:189`) shows a `diff <a> <b>` command, but the command switch (`:47-53`) has no `diff` case — documented-but-unimplemented command.
- No `NotImplementedException` present; the gaps are TODO comments, missing files, and dead code rather than throws.

---

## 2. What was actually run

**Committed evidence of prior runs: essentially none.**
- `tmppm-data/` (downloaded PDFs) is **gitignored** (`.gitignore`). No manuals committed.
- **No extracted-rule artifacts, fixtures, or output JSON are committed anywhere** (only `appsettings.json` config files exist; no `tmppm-data` dir committed).
- All pipeline output goes to a **live Mongo/Cosmos DB**, which is not in the repo and which this audit does not touch. The portal consumes it at runtime from Mongo (`TmppmIndexService.cs` builds indexes on `tmppm_pa_rules`), not from checked-in files.

**How many chapters were processed — cannot be confirmed from committed artifacts.** What *can* be confirmed:
- The pipeline is **configured for exactly 8 chapters** — hardcoded `KnownChapters` (`TmhpChapterDownloader.cs:18-28`): 1 Vol. 1 section + 7 Vol. 2 handbooks:
  1. `1_05_prior_authorization` — **Vol. 1, Section 5**
  2. `2_01_ambulance_services` — Vol. 2 handbook
  3. `2_02_behavioral_health` — Vol. 2 handbook
  4. `2_06_dme_and_supplies` — Vol. 2 handbook
  5. `2_11_inpatient_outpatient_hosp_srvs` — Vol. 2 handbook
  6. `2_13_med_specs_and_phys_srvs` — Vol. 2 handbook
  7. `2_16_pt_ot_st_srvs` — Vol. 2 handbook
  8. `2_17_radiology_and_lab_srvs` — Vol. 2 handbook
- The only run-like evidence is a **README prose table of 4 hand-checked sections** (`README.md:236-241`), **all inside a single chapter (2_13)** — §9.2.46.14, §9.2.8.1, §9.2.33.1, §9.2.51.1. These are described in text, with no accompanying output artifact.

**Coverage map vs. full TMPPM structure** (structure/counts only, no manual text):

| TMPPM segment | Full structure (approx.) | Wired in pipeline | Confirmed processed (committed artifacts) |
|---|---|---|---|
| Vol. 1 (General/FFS) | ~8 sections | 1 (Section 5 only) | **Unknown** |
| Vol. 2 (Handbooks) | ~20+ handbooks | 7 (a non-contiguous curated subset: 01, 02, 06, 11, 13, 16, 17) | **Unknown** (4 sections of handbook 2_13 spot-checked per README prose) |
| **Total** | **~28+ chapters/handbooks** | **8 configured** | **0 confirmed from repo; ≤1 chapter partially exercised anecdotally** |

So "8 chapters" describes the **configured target list**, not a contiguous "first 8," and there is no committed proof any of them were run end-to-end.

**Dates / staleness:** The entire tool was **added in a single commit `2227e23` on 2026-07-28** (PR #1049 "Fix Cosmos MongoDB application compatibility"). Only **one commit** ever touches the directory. As of 2026-08-06 the pipeline is **~9 days old** — not something run "a while ago."

---

## 3. Deterministic vs. model fallback — did the model ever fire?

**The model could never have fired: there is no model code path.**
- The LLM fallback (`LlmAssistedParser` / `HybridParser`) **does not exist** — the files are referenced only in the README (§1 table above). No Anthropic package, no API client, no `messages.create`, no `x-api-key` anywhere in the tool.
- `IngestionPipeline.ExtractRulesFromChapter` (`IngestionPipeline.cs:95-145`) calls **only** the regex methods of `TmppmPdfParser` — `ExtractProcedureCodes`, `ExtractAgeRule`, `ExtractDiagnosisCodes`, `DetectPaRequired`. There is no confidence/low-probability branch and no fallback invocation of any kind.

**Trigger for fallback:** none exists. The "low-probability/confidence decision" described in the recollection and README is **not implemented** — there is no confidence computation to threshold on.

**Conclusion:** For the code as committed, extraction is **100% deterministic regex by construction.** Whether the pipeline was ever *run at all* on the chapters cannot be determined from committed artifacts (outputs live only in an external DB) — but even if it was, the model fallback **cannot** have fired, because it isn't there.

---

## 4. Output shape — what an extracted rule looks like

Rule shape = `TmppmPaRule` (`Models/TmppmModels.cs:7-29`), populated in `IngestionPipeline.cs:119-136`.

| Field asked about | Status | Evidence |
|---|---|---|
| **(a) extraction_tier / source-of-resolution** (deterministic vs. model) | **ABSENT** | No such field on `TmppmPaRule`. `RuleType` is a clinical category ("AuthRequired"…), not a provenance tier. Every rule is deterministic anyway (§3), and nothing records that. |
| **(b) per-rule confidence score** | **ABSENT** | No confidence/score field exists on `TmppmPaRule`. |
| **(c) verbatim source span** | **PARTIAL / coarse** | Two weak pointers: `TmppmRef` = `§<section>` (`IngestionPipeline.cs:124`), and `ClinicalCriteriaSummary` = **first 500 chars of the section text, truncated with "…"** (`IngestionPipeline.cs:133-135`). No page number, no character offsets, no exact span boundaries, and it's truncated — so it is *not* a faithful verbatim span you could anchor a benchmark to. |

**Benchmark implication:** As-is, a hallucination/calibration benchmark **could not** be built without changing the extractor. There is no tier label, no confidence to calibrate, and only a truncated 500-char snippet + section number as a provenance anchor.

---

## 5. Was fidelity ever checked?

**No faithfulness verification exists. Only anecdotal "it ran" evidence.**
- **No test project or test files for this tool** (no `*tmppm*` test files; no test references `TmppmPdfParser`/`IngestionPipeline`/`TmhpChapterDownloader`/`TmppmRuleStore`). The tool isn't in the solution, so it isn't in CI.
- **No gold set, no validation artifact, no spot-check fixture** committed.
- The only fidelity-adjacent evidence is the README's "Validated extractions" prose table (`README.md:232-241`) — 4 sections a human eyeballed, stated as claims (e.g., "64583/64584 do NOT require PA"), with **no reproducible artifact** and no assertion harness.

**Verdict:** This is "**ran clean (anecdotally)**," not "**output verified correct.**" There is no evidence any extracted rule was systematically checked for faithfulness to the source, and no automated regression exists even for the crash-free case.

---

## 6. Monthly-update / refresh handling

**Partial — change *detection* is wired; change *diffing/regression* is not.**
- ✅ **Present:** SHA256-based change detection. `TmhpChapterDownloader.DownloadEditionAsync` hashes each PDF (`:65`); `DetectChangedChapters` compares against the prior edition's stored hashes (`:92-113`); `IngestionPipeline` re-parses only changed chapters (`:39-64`). Edition metadata with per-chapter SHA256 is persisted for next month (`SaveEditionAsync`, `TmppmRuleStore.cs:117`).
- ⚠️ **Partial / stubbed:** the diff report — the actual month-over-month delta computation is a **TODO** (`IngestionPipeline.cs:71-74`). `TmppmDiffReport`/`TmppmRuleDelta` models and `SaveDiffReportAsync` exist but are **never invoked** (dead code). So "which rules were added/modified/removed" is **not produced**.
- ❌ **Absent:** any output-regression check across manual versions (no baseline comparison of extracted rules; the `RequiresHumanReview` flag is never set).
- **Deployment:** the monthly AKS `CronJob` exists only as **YAML in the README** (`README.md:264-292`); no k8s manifest for it is committed, and the tool isn't in the solution or CI.

---

## Verdict — confirms / corrects your recollection

**Your memory: "we ran it on the first ~8 chapters a while ago and it digitalized them with regex alone (no model fallback); extending to all ~20 + monthly updates was the next step."**

- **"regex alone (no model fallback)" — CONFIRMED, but for a stronger reason than you remembered.** It wasn't regex-alone because the model happened not to fire on low-probability rules — it's regex-alone because **the model fallback was never built.** `LlmAssistedParser` and `HybridParser` exist only as README fiction; there's no Anthropic dependency, config, or code path. The "hybrid regex + LLM" architecture is aspirational documentation, not implemented behavior.

- **"~8 chapters" — PARTIALLY CONFIRMED (as a target, not a proven run).** The pipeline is hardcoded to exactly **8 chapters** — but they're **1 Vol. 1 section + 7 curated Vol. 2 handbooks**, not "the first 8." And there is **no committed evidence any were actually processed** — all output lives in an external DB, and the only run-evidence in the repo is a prose table of **4 hand-checked sections inside one chapter (2_13)**.

- **"a while ago" — CORRECTED.** The entire tool landed in **one commit on 2026-07-28** (≈9 days before this audit). It's new, not old.

- **"extending to all ~20 + monthly updates was the next step" — PARTIALLY IN PLACE.** Monthly **change detection** (SHA256) is real and wired; the **diff/delta report** and any **version-over-version regression** are stubbed TODO/dead code; the CronJob is README-only. Extension to the full ~28 handbooks would just be growing `KnownChapters`, but the missing LLM enrichment means criteria-only sections (e.g., bariatric §9.2.8.1, flagged in the README as needing "LLM enrichment") currently yield **zero procedure codes** and therefore **zero ConceptMap overrides** (`TmppmRuleStore.cs:60` filters to `ProcedureCodes.Count > 0`).

**Bottom line:** The deterministic regex layer is real and is the *entire* extractor. The model-fallback tier you remember does not exist in code. There is **no committed proof of the "8 chapter" run**, **no confidence/tier/precise-span fields** to support a later hallucination/calibration benchmark, and **no fidelity check** beyond four manual spot-checks described in prose. The pipeline is ~9 days old, not a mature prior effort.
