# Dependency audit exceptions

`Dependency Security Scan` gates every pull request on `audit-ci`, configured
by `.audit-ci.json`: production dependencies, moderate and above. An entry in
that file's `allowlist` waives one advisory, and `.audit-ci.json` is JSON, so
it cannot carry the reason beside the entry. This file is where the reason
lives.

**An entry here is not a judgement that an advisory is wrong.** It is a
statement that the vulnerable code path is not reachable as CHO uses the
package, written down so that the next person can check the claim rather than
inherit it. Every entry names the code that establishes it and the condition
that removes the entry again.

Scope an entry to the dependency path wherever possible
(`<GHSA>|<parent>><child>`), not to the advisory alone: a bare advisory id
would also waive the same flaw arriving through a different package later.

Review these whenever the scan is touched. `audit-ci` prints
`Consider not allowlisting advisory: <id>` for an entry that no longer matches
anything, which is the signal to delete it.

## Two scanners read the same lockfile

`Security Scan` runs `audit-ci` in `Dependency Security Scan` **and** Trivy in
`Docker Image Security Scan`, and neither reads the other's configuration.
Trivy keys on the CVE id where npm's advisory database keys on the GHSA id, it
usually learns about an advisory some days later, and it scopes an ignore to
the *file* the dependency was found in rather than to the dependency path. So
waiving an advisory in `.audit-ci.json` leaves the build red on the other job
until the same decision is written into `.trivyignore.yaml`.

A waiver therefore has three parts, and is not finished without all three: the
`.audit-ci.json` entry, the `.trivyignore.yaml` entry, and the section in this
file that both point at. Trivy prints nothing when an entry stops matching, so
its entries carry `expiredAt` — a date CHO has to look again, not a guess at
when upstream will fix it.

---

## GHSA-xcx6-jx8p-x3h5 — `@azure/identity` > `@azure/msal-node`

**Advisory.** [MSAL Node authorization code flow may disclose parameters to an
attacker-controlled endpoint](https://github.com/advisories/GHSA-xcx6-jx8p-x3h5).
High, CVSS 7.5, CWE-346. Affects `@azure/msal-node` `>=5.0.0-beta.0 <7.1.0`.

**Why it is not reachable here.** The flaw is in the authorization code flow,
which is the interactive, browser-redirect path. CHO's only consumer of this
package is `src/fhir/payer-to-payer-api.ts`, which constructs
`DefaultAzureCredential` and nothing else. That credential resolves through
managed identity, environment and workload-identity credentials; it never
performs an authorization code exchange. No CHO code calls
`AuthorizationCodeCredential`, `InteractiveBrowserCredential` or
`@azure/msal-node` directly.

**Why it is not simply fixed.** `@azure/identity` 4.14.0 is the latest
published release and pins `@azure/msal-node: ^6.0.0`, below the fixed 7.1.0.
There is no version of `@azure/identity` that takes a fixed msal-node, so no
upgrade clears it. `npm audit fix --force` proposes a *downgrade* to
`@azure/identity` 4.13.0, which trades a current auth library for an older one
to avoid a flow CHO does not use. Forcing msal-node 7.x through an `overrides`
entry would jump `@azure/identity`'s own dependency a full major past what it
declares, untested against the payer-to-payer path.

**Remove this entry when** `@azure/identity` publishes a release depending on
`@azure/msal-node` 7.1.0 or later; bump to it and delete **both** the
`.audit-ci.json` allowlist line and the `.trivyignore.yaml` entry.

**Re-check this entry if** anything in CHO starts using an interactive or
authorization-code Azure credential, at which point the advisory becomes live
and the allowlist must go whatever upstream has done.

Added 24 October 2026. Trivy reports the same advisory as **CVE-2026-109461**
and began doing so later the same day, which is why the Docker Image Security
Scan went red after the audit-ci waiver had already gone in; the
`.trivyignore.yaml` entry expires 24 April 2027.
