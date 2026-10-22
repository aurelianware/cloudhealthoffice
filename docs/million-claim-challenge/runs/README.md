# Million Claim Challenge run evidence

One folder per run, named after the tenant the run used. Each holds the data
behind that run's headline numbers, so anyone can check them claim by claim:

| File | What it is |
| --- | --- |
| `run.json` | The run summary the validator persisted: claim counts, throughput and latency, the per-scenario workflow breakdown, payment comparisons and deltas, seed and parallelism |
| `not-matched.json` | Every scored claim that did **not** match: `Mismatched` (observed outcome differs from the expected one), `ObservationTimeout` (finished after the validator's window) and `Unsupported`. Each row has the scenario, expected and observed outcome, expected and observed denial code, expected and actual plan payment, and timings |
| `validator.log` | The validator's console output, when the run was exported by `scripts/run-mcc-local-k8s.sh` |

The corpus is synthetic: claims, members and providers come from a seeded
generator, so the same seed reproduces the same claims. No real member or
provider data is in these files.

## Producing it

`scripts/run-mcc-local-k8s.sh` writes the folder at the end of every run
(`EXPORT_EVIDENCE=false` skips it). For an earlier run still in a local
cluster's database:

```bash
TENANT=mcc-1m-confirmation-v2 KUBE_CONTEXT=docker-desktop scripts/export-mcc-evidence.sh
```

Each mismatch is then classified in the episode's results as one of:
a platform defect (fixed, with the fix referenced), an error in the expected
outcome (the generator or scenario corrected), or an observation artifact
(a claim that finished correctly after the window).
