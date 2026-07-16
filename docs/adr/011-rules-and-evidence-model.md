# ADR 011: Separate Rules, Scoring, And Claims

## Status

Accepted

## Context

Healthcare adjudication rules, benchmark answer keys, validation outcomes, and
marketing claims can easily be conflated. That creates risk: a dashboard can
make unsupported behavior look successful, or a benchmark can hide a mismatch
behind a summary.

## Decision

Keep adjudication rules, benchmark scoring, and public claims separate:

- Adjudication rules determine claim outcomes.
- Benchmark answer keys define expected synthetic outcomes.
- Validators score what can be observed honestly.
- Public docs report evidence, limitations, and dated results.

## Consequences

Positive:

- Unsupported scenarios remain visible as work, not wins.
- Payment accuracy can be scored separately from workflow disposition.
- The Million Claim Challenge can improve platform credibility without
  weakening correctness gates.

Tradeoffs:

- More categories appear in benchmark reports.
- Documentation must explain limitations instead of compressing results into one
  green number.

## References

- [Million Claim Challenge benchmarks](../benchmarks/README.md)
- [Episode 008 100K result](../million-claim-challenge/podcast/episode-008/article.txt)
- [Pended-claim validation](../million-claim-challenge/pend-validation.md)
