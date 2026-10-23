#!/usr/bin/env node
//
// Seeds the interop-cho profile's services with the synthetic record set in
// interop/cho-seed.json.
//
//   ./scripts/interop/seed-cho.mjs
//   ./scripts/interop/seed-cho.mjs --verify-only
//
// Writes go through each service's own HTTP API, never into Mongo behind it, so
// a seed passes exactly the validation a real write does. A fixture that the
// services would reject fails here rather than producing a resource no caller
// could have created — which is the point: an Inferno suite reading seeded data
// must be reading data CHO itself accepted.
//
// Authentication is a development-signed user token. The key is the public
// development symmetric key every service already carries in its
// appsettings.Development.json, and ChoAuthOptions.Validate refuses symmetric
// keys on any host that is not Development or Testing, so this script cannot
// authenticate against anything deployed. Permissions are named explicitly
// rather than taken from a role, because a role expands through
// ChoRolePermissions and what it expands to is not this script's business.
//
// No dependencies: Node's own crypto signs the token.

import { createHmac } from 'node:crypto';
import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const REPO_ROOT = join(dirname(fileURLToPath(import.meta.url)), '..', '..');
const FIXTURE = join(REPO_ROOT, 'interop', 'cho-seed.json');

// Mirrors ChoDevelopmentAuth. Base64, as ChoAuthOptions.SigningCredentialsFrom reads it.
const DEV_KEY = process.env.CHO_DEV_SIGNING_KEY
  ?? 'Q0hPLWRldmVsb3BtZW50LW9ubHktc2lnbmluZy1rZXktZG8tbm90LXVzZS0yMDI2';
const DEV_ISSUER = 'cho-portal-dev';
const DEV_AUDIENCE = 'cho-api';

const SERVICES = {
  member: process.env.INTEROP_MEMBER_URL ?? 'http://127.0.0.1:18025',
  coverage: process.env.INTEROP_COVERAGE_URL ?? 'http://127.0.0.1:18026',
  claims: process.env.INTEROP_CLAIMS_URL ?? 'http://127.0.0.1:18027',
  fhir: process.env.INTEROP_FHIR_URL ?? 'http://127.0.0.1:18023',
};

const PERMISSIONS = [
  'members:read', 'members:write',
  'coverage:read', 'coverage:write',
  'claims:read', 'claims:work',
];

const base64url = (buffer) =>
  Buffer.from(buffer).toString('base64').replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');

function devUserToken(tenantId) {
  const now = Math.floor(Date.now() / 1000);
  const header = { alg: 'HS256', typ: 'JWT' };
  const payload = {
    iss: DEV_ISSUER,
    aud: DEV_AUDIENCE,
    sub: `interop-seed-${tenantId}`,
    jti: `${now}-${Math.random().toString(36).slice(2)}`,
    iat: now,
    nbf: now,
    exp: now + 900,
    tenant_id: tenantId,
    roles: [],
    permissions: PERMISSIONS,
    name: 'Interop Seed',
  };
  const signingInput =
    `${base64url(JSON.stringify(header))}.${base64url(JSON.stringify(payload))}`;
  const signature = createHmac('sha256', Buffer.from(DEV_KEY, 'base64'))
    .update(signingInput)
    .digest();
  return `${signingInput}.${base64url(signature)}`;
}

/**
 * Strips the keys that belong to the fixture rather than to a CHO API: the
 * _comment lines it explains itself with, and the expectProfile each claim
 * declares for the verify pass.
 */
const FIXTURE_ONLY_KEYS = new Set(['_comment', 'expectProfile']);

function asPayload(value) {
  if (Array.isArray(value)) return value.map(asPayload);
  if (value && typeof value === 'object') {
    return Object.fromEntries(
      Object.entries(value)
        .filter(([key]) => !FIXTURE_ONLY_KEYS.has(key))
        .map(([key, v]) => [key, asPayload(v)]));
  }
  return value;
}

async function call(method, url, token, body, accept = 'application/json') {
  const response = await fetch(url, {
    method,
    headers: {
      Accept: accept,
      Authorization: `Bearer ${token}`,
      ...(body === undefined ? {} : { 'Content-Type': 'application/json' }),
    },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  const text = await response.text();
  return { status: response.status, text };
}

/**
 * Writes one resource. A 409 is success: the record is already there, which is
 * what a re-run of a seed should mean. Anything else fails loudly with the
 * service's own message — a seed that half-applied is worse than one that stopped.
 */
async function put(label, method, url, token, body) {
  const { status, text } = await call(method, url, token, body);
  if (status === 409) {
    console.log(`    ${label}: already present (409)`);
    return;
  }
  if (status < 200 || status >= 300) {
    throw new Error(`${label}: ${method} ${url} answered ${status}\n${text.slice(0, 2000)}`);
  }
  console.log(`    ${label}: ${status}`);
}

async function seed(fixture, token) {
  console.log(`==> member-service ${SERVICES.member}`);
  await put('member', 'POST', `${SERVICES.member}/api/v1/members`, token, asPayload(fixture.member));

  console.log(`==> coverage-service ${SERVICES.coverage}`);
  // coverage-service answers 201 to a second identical POST rather than 409, so
  // a re-run would stack duplicate coverage on the member. Ask first.
  const existing = await call(
    'GET', `${SERVICES.coverage}/api/v1/coverage/member/${fixture.coverage.memberId}/history`, token);
  const alreadyCovered = existing.status === 200 && JSON.parse(existing.text).length > 0;
  if (alreadyCovered) {
    console.log('    coverage: already present');
  } else {
    await put('coverage', 'POST', `${SERVICES.coverage}/api/v1/coverage`, token, asPayload(fixture.coverage));
  }

  console.log(`==> claims-service ${SERVICES.claims}`);
  for (const claim of fixture.claims) {
    // Claim submission is not idempotent either — a second POST of the same
    // claim number is a legitimate resubmission and gets its own claim — so a
    // re-run would double the EOBs a suite reads. Ask by claim number first.
    const existing = await call(
      'GET', `${SERVICES.claims}/api/claims/number/${claim.claimNumber}`, token);
    if (existing.status === 200) {
      console.log(`    ${claim.claimNumber}: already present`);
      continue;
    }
    if (existing.status !== 404) {
      throw new Error(
        `${claim.claimNumber}: the lookup answered ${existing.status}, so whether it is ` +
        `already seeded is unknown. Refusing to submit rather than risk a duplicate.\n` +
        existing.text.slice(0, 2000));
    }
    await put(claim.claimNumber, 'POST', `${SERVICES.claims}/api/v1/claims`, token, asPayload(claim));
  }
}

/**
 * Reads the seeded data back as the FHIR a suite would read, which is the only
 * check that matters: the services accepting a write does not mean the FHIR
 * path serves it, and it certainly does not mean it serves the right profile.
 */
async function verify(fixture, token) {
  const patientUrl = `${SERVICES.fhir}/fhir/r4/Patient/${fixture.member.memberId}`;
  console.log(`==> verifying through fhir-service ${patientUrl}`);
  const patient = await readFhir(patientUrl, token, 'the seeded member');
  expectProfile(
    patient, 'http://hl7.org/fhir/us/core/StructureDefinition/us-core-patient', 'Patient');
  console.log(`    Patient/${patient.id} — us-core-patient`);

  // The Provider Access payload itself. A claim that lands on the wrong CARIN
  // profile is a silently weaker answer, not an error — the projector falls to
  // the outpatient profile for any institutional claim it cannot read as
  // inpatient — so each claim names the profile it must produce.
  const eobUrl =
    `${SERVICES.claims}/fhir/ProviderAccess/ExplanationOfBenefit?patient=${fixture.member.memberId}`;
  console.log(`==> verifying the Provider Access EOBs ${eobUrl}`);
  const bundle = await readFhir(eobUrl, token, 'the Provider Access EOB search');
  const served = (bundle.entry ?? []).flatMap((e) => e.resource?.meta?.profile ?? []);

  for (const claim of fixture.claims) {
    if (!claim.expectProfile) continue;
    if (!served.includes(claim.expectProfile)) {
      throw new Error(
        `${claim.claimNumber} should project to ${claim.expectProfile}, but the search ` +
        `returned ${served.length ? served.join(', ') : 'nothing'}.`);
    }
    console.log(`    ${claim.claimNumber} — ${claim.expectProfile.split('/').pop()}`);
  }
}

async function readFhir(url, token, what) {
  const { status, text } = await call('GET', url, token, undefined, 'application/fhir+json');
  if (status !== 200) {
    throw new Error(`fhir read of ${what} answered ${status}.\n${text.slice(0, 2000)}`);
  }
  return JSON.parse(text);
}

function expectProfile(resource, profile, label) {
  const profiles = resource.meta?.profile ?? [];
  if (!profiles.includes(profile)) {
    throw new Error(`${label} came back without ${profile}: ${JSON.stringify(profiles)}`);
  }
}

const fixture = JSON.parse(readFileSync(FIXTURE, 'utf8'));
const token = devUserToken(fixture.tenantId);

if (!process.argv.includes('--verify-only')) {
  await seed(fixture, token);
}
await verify(fixture, token);
console.log('==> seeded');
