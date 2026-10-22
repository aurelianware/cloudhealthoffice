# ADR 026: Bank-confirmed claims ACH settlement in the GL

Status: proposed implementation for native-core acceptance, October 22, 2026.

A BAI2 match currently records a NACHA settlement without moving ACH-in-transit
to cash. Add one deterministic PaymentFileSettled event in the same insert-only
settlement document, dispatched through the existing durable GL outbox reader.
A crash after insertion must leave the event recoverable without rematching the
already-settled file. Do not emit only after the settlement write.

Scope: transmitted, credits-only claims files whose bank debit equals the whole
file total. Debit/mixed files and returns remain manual/unimplemented accounting
seams. Earlier settlements are not automatically backfilled; finance must
reconcile opening balances/cutover before any historical event generation.

AR posts Dr ACH-in-transit / Cr cash at the bank statement date, once per payment
run. Require the exact transmission event to have posted with matching amount,
run and file first. Out-of-order settlement parks and is retried through the
existing dependency mechanism. Invalid/mismatched data parks; duplicate delivery
cannot duplicate cash. Closed-period/cutover/account-mapping controls remain.
Cash and transit must map to different accounts for this new posting; otherwise
park for mapping correction, preserving existing deployments' startup behavior.

Acceptance: durable outbox discovery/marking across restart and tenants; matched
credit settlement, replay, mismatch and unsupported direction; balanced entries,
out-of-order dispatch, redelivery, wrong transmission, missing mappings, period
close and cutover. This is one financial boundary, not complete premium,
capitation, returns, aging or payer-close implementation.
