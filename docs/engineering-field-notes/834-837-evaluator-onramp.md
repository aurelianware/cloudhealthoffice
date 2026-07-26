# The Claim That Would Never Price: Building the 834-to-837 Evaluator On-Ramp

The pitch to an evaluator is simple to say and hard to earn: drop the same enrollment and claims files you'd hand your existing core admin platform, and see what CloudHealthOffice does with them. No synthetic data, no pre-populated internal IDs, no cooperation required from the incumbent system. Just the files.

That pitch had been half-built for a while. The 834 enrollment parser existed. The 837 claims parser existed, tested against real-world variance in segment structure. Both had on-ramps — `POST /import/raw834`, `POST /import/raw837` — sitting on live controllers, documented as exactly this: the path for an evaluator to drop their own file rather than call a structured JSON API. What hadn't been checked was whether the two halves actually agreed with each other. Whether a member seeded through the 834 side could be found by a claim submitted through the 837 side, and whether that claim could reach an actual dollar amount instead of stalling somewhere in between.

It couldn't. Not because either parser was broken — because of a gap between them that no single-service test would ever catch.

## Two on-ramps, two philosophies, one silent assumption

The 834 pipeline (enrollment-import-service) and the 837 pipeline (claims-service) were each built with a different relationship to guessing. enrollment-import-service's mapper had no resolution step at all: it wrote whatever free-text plan code the trading partner's own file happened to carry (`"Blue Shield PPO"`, or similar — a string a broker's HRIS assigned, meaningless outside that broker's own system) straight into the member's coverage record as if it were CloudHealthOffice's own internal plan ID, falling back to the literal string `"DEFAULT"` only when even that was missing. claims-service's 837 mapper, on the other hand, deliberately left `BenefitPlanId` blank rather than guess at it, reasoning — correctly — that an unrecognized member should surface as a real pend during adjudication, not get papered over during mapping.

One gap and one correct instinct, on either side of a boundary neither service could see across. The 834 side's missing resolution step meant coverage records existed but pointed nowhere real. The 837 side's honest blank meant every claim needed something to resolve `BenefitPlanId` from — and nothing did. `BenefitCalculationStage`, the actual pricing stage in claims-service's eight-stage adjudication pipeline, hard-rejects any claim that arrives without one:

```
if (string.IsNullOrWhiteSpace(claim.BenefitPlanId))
{
    return ClaimAdjudicationStageResult.Reject(
        StageName,
        "Claim is missing BenefitPlanId; benefit calculation cannot run.");
}
```

Correct in isolation. Fatal in combination. An evaluator could enroll a member perfectly, submit a claim for that exact member, and watch it pend before pricing — regardless of how correctly they'd done everything upstream. Nothing was broken in a way any one service's test suite would flag; both halves were doing exactly what they were built to do.

## Fixing the silent default first

The first fix wasn't in the 837 pipeline at all — it was making sure enrollment ever produced a real plan ID for anything to resolve. `Coverage.PlanId` was just echoing the 834's own free-text plan code (`"Blue Shield PPO"`, assigned by whatever trading partner sent the file) straight through, with `"DEFAULT"` as the fallback when even that was missing. There was no mapping between "what the trading partner calls this plan" and "what CloudHealthOffice calls this plan" — because that mapping had never existed.

Building it meant standing up a crosswalk: `(TenantId, GroupNumber, InsuranceLineCode, ExternalPlanCode) → PlanId`, owned by benefit-plan-service since that's the system of record for what a valid plan actually is. enrollment-import-service now resolves through it before writing coverage, and refuses to fall back to a fake default — an unresolved code is now a visible, countable gap (`CoverageMappingsUnresolved`) instead of a silently wrong write. Alongside it: a bulk-import endpoint and a gap-report tool that scans a sample 834 file and reports exactly which plan codes still need mapping before an employer group's real files can be trusted — the actual onboarding checklist, generated from the file itself rather than assembled by hand.

While in that code, the last piece of enrollment-import-service still writing directly into a Mongo collection it didn't own — Coverage, shared with coverage-service's own repository — got delegated the same way Member and Sponsor already had been. One collection, one owner, finally, for all four entities the 834 pipeline touches.

## Finding the second gap

With real plan IDs flowing from enrollment, the question became whether an 837 claim could actually find one. It couldn't — not automatically. `ClaimAdjudicationOrchestrator` resolved `BenefitPlanId` into a full plan record *if* the claim already had one, and resolved the submitting member's demographics unconditionally, but nothing sat between those two steps asking coverage-service what plan a member with no `BenefitPlanId` was actually covered under.

The fix mirrors two resolvers that already existed for exactly this shape of problem — `IMemberResolver` and `IBenefitPlanResolver`, each a thin HTTP client with a five-minute caching decorator. A third, `ICoverageResolver`, now asks coverage-service for the member's active coverage by service date and insurance line, and the orchestrator calls it exactly once, exactly when needed:

```
if (string.IsNullOrWhiteSpace(claim.BenefitPlanId) && !string.IsNullOrWhiteSpace(claim.MemberId))
{
    var resolvedPlanId = await _coverageResolver.ResolveBenefitPlanIdAsync(
        message.TenantId, claim.MemberId, claim.ServiceDateFrom,
        MapInsuranceLineCode(claim.ClaimType), ct);

    if (!string.IsNullOrWhiteSpace(resolvedPlanId))
    {
        claim.BenefitPlanId = resolvedPlanId;
    }
}
```

Claims that already carry a `BenefitPlanId` — structured JSON submissions, synthetic benchmark claims — pass through untouched. Claims that don't, and belong to a member with real coverage, now reach `BenefitCalculationStage` with something to price against instead of a guaranteed rejection.

## Proving the seam, not just the two halves

Neither service's own test suite could have caught the original gap, because the gap lived *between* them. So the verification had to as well: a script that seeds a real benefit plan, seeds a plan-code mapping, imports an actual 834 fixture file, submits a matching 837 for the same member, and polls the resulting claim until adjudication settles — asserting on exactly one thing, the specific failure this closes: `BenefitPlanId` must resolve. Whatever the claim's final outcome is beyond that — approved, denied, pended for some unrelated and legitimate reason — gets reported, not asserted on. Proving the plumbing connects is a different claim than proving every pricing rule is correct, and conflating the two would have made the test lie about what it actually verifies.

That fixture discipline caught something else along the way, for free: a hand-authored 837 payload used by the smoke script got its own parser-level test, specifically to confirm the shell script's embedded EDI text was well-formed *before* anyone ran it against a live stack and got a confusing curl failure instead of a clear one.

## Making the seam visible after the fact

A working pipeline that nobody can inspect is still a black box to the person evaluating it. Both import paths now write a transaction record — accepted or rejected, either way — to a log an admin endpoint can page through, closing a gap that had existed silently on the 837 side the whole time: a rejected claim's error message used to disappear the moment the synchronous HTTP response did. The portal's new EDI Transactions console, built on the same master-list pattern as the existing Mass Adjudication console, puts both logs — 834 and 837 — in one place: what was dropped, when, and whether it was accepted, with the rejection reason inline when it wasn't.

None of this closes the loop by itself. It makes the loop inspectable, which is the precondition for anyone — an evaluator, or CloudHealthOffice's own engineers — trusting what it reports.

## Where this connects to the Million Claim Challenge

MCC has never touched raw EDI. Every claim it submits arrives as structured JSON with `BenefitPlanId` already populated — a deliberate choice, made so the benchmark could isolate adjudication correctness from EDI parsing correctness, and it's the right choice for what MCC measures. But it also meant the exact gap this work closes — a real member, enrolled correctly, submitting a real 837, never reaching a priced outcome — was invisible to a million-claim run and would have stayed invisible no matter how many times that run went clean.

The two efforts test different things and always will. MCC proves the adjudication engine gets the right answer at scale. This proves the on-ramp in front of it doesn't quietly break correctness before adjudication ever starts — the part of the pipeline an actual evaluator's files touch first, and the part no synthetic corpus with pre-resolved IDs was ever going to exercise. Scaling the real on-ramp itself — enough concurrent 834 and 837 file drops to know whether coverage resolution holds up under the same load MCC already proved the adjudication engine can take — is the next place these two lines of work actually meet.
