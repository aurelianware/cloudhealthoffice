# Texas Medicaid demo, on demand

Synthetic Texas Medicaid plan on the GKE demo cluster, off until someone needs
it. Runbook, costs and the 15-minute click-through:
[docs/deploy/TEXAS-MEDICAID-DEMO.md](../../../docs/deploy/TEXAS-MEDICAID-DEMO.md).

| File | What it does |
|---|---|
| `demo.sh` | `up`, `down`, `status`, `connect`, `seed`, `overlay`, `drop` against the cluster |
| `tx_demo.py` | `generate` a day's CPCDS drop, `setup` plans and providers, `seed-pa` the Texas clocks, `report`, `fhir` |
| `fixtures/` | The three synthetic benefit plans and the fee schedule |
| `test_tx_demo.py` | `python3 -m pytest scripts/demo/texas/test_tx_demo.py` |

The same steps run from GitHub: Actions → **Texas Medicaid demo (on / off)**.
