# Texas (TMHP) encounter sample files

Synthetic 837 encounter interchanges as CHO writes them for TMHP. Every member,
provider, plan, submitter ID, payer ID and ICN here is made up. The only real
identifier is TMHP's public test receiver ID `617591011C21T` (EDI
Connectivity Guide, revised July 28, 2026, §2.2).

| File | Contents |
| --- | --- |
| `TMHP_SYNTH_837P_TEST.dat` | 837P (005010X222A1): an original, and a void (CLM05-3 `8`, `REF*F8` ICN) |
| `TMHP_SYNTH_837I_TEST.dat` | 837I (005010X223A2): an inpatient stay with POA on every diagnosis |
| `TMHP_SYNTH_837D_TEST.dat` | 837D (005010X224A2): an exam and a two-surface filling, and a replacement (CLM05-3 `7`) |

`TexasSampleFileTests` rebuilds each file from synthetic encounters, runs the
Texas validator and TMHP structure checks (one GS group, at most 5,000
transactions, CLM01 of 20 characters or fewer, SE counts, BHT06 `RP`), and
fails if the output changes. After an intended writer change, regenerate with:

```bash
UPDATE_TEXAS_SAMPLES=1 dotnet test tests/CloudHealthOffice.EncounterSubmissionService.Tests --filter TexasSampleFileTests
```

Segments are on one line, with `~` as the terminator, as TMHP receives them.
To read one: `tr '~' '\n' < TMHP_SYNTH_837D_TEST.dat`.

These files have not been through a TMHP test cycle. The 2010BB payer ID,
the submitter ID, and anything in the TMHP encounter companion guide (sent
only to contracted plans) still have to be confirmed with the first plan.
