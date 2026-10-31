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
// Authentication is a development-signed user token, described by the fixture's
// own `auth` block rather than by constants here — the interop harness mints the
// same token from the same block, and a drift between the two would surface as
// an opaque 401 rather than as a diff.
//
// No dependencies: Node's own crypto signs the token.

import { createHmac } from 'node:crypto';
import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const REPO_ROOT = join(dirname(fileURLToPath(import.meta.url)), '..', '..');
const FIXTURE = join(REPO_ROOT, 'interop', 'cho-seed.json');

const fixture = JSON.parse(readFileSync(FIXTURE, 'utf8'));

const SERVICES = {
  member: process.env.INTEROP_MEMBER_URL ?? 'http://127.0.0.1:18025',
  coverage: process.env.INTEROP_COVERAGE_URL ?? 'http://127.0.0.1:18026',
  claims: process.env.INTEROP_CLAIMS_URL ?? 'http://127.0.0.1:18027',
  encounter: process.env.INTEROP_ENCOUNTER_URL ?? 'http://127.0.0.1:18028',
  fhir: process.env.INTEROP_FHIR_URL ?? 'http://127.0.0.1:18023',
  provider: process.env.INTEROP_PROVIDER_URL ?? 'http://127.0.0.1:18029',
  benefitPlan: process.env.INTEROP_BENEFIT_PLAN_URL ?? 'http://127.0.0.1:18030',
};


const base64url = (buffer) =>
  Buffer.from(buffer).toString('base64').replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');

/**
 * Mints the development user token the fixture's `auth` block describes. The
 * signing key is the public development symmetric key every service already
 * carries in its appsettings.Development.json; ChoAuthOptions.Validate refuses
 * symmetric keys on any host that is not Development or Testing, so a token
 * minted here cannot authenticate against anything deployed.
 */
function devUserToken(auth, tenantId) {
  const key = process.env[auth.signingKeyEnvVar] ?? auth.signingKeyBase64;
  const now = Math.floor(Date.now() / 1000);
  const header = { alg: 'HS256', typ: 'JWT' };
  const payload = {
    iss: auth.issuer,
    aud: auth.audience,
    sub: `${auth.subjectPrefix}-${tenantId}`,
    jti: `${now}-${Math.random().toString(36).slice(2)}`,
    iat: now,
    nbf: now,
    exp: now + auth.lifetimeSeconds,
    tenant_id: tenantId,
    roles: [],
    permissions: auth.permissions,
    name: 'Interop Seed',
  };
  const signingInput =
    `${base64url(JSON.stringify(header))}.${base64url(JSON.stringify(payload))}`;
  const signature = createHmac('sha256', Buffer.from(key, 'base64'))
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
      ...(token === null ? {} : { Authorization: `Bearer ${token}` }),
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

/**
 * The first member with its coverage and claims, then each of
 * additionalMembers. Suites judge the first; the others exist for checks
 * that need more than one patient (the Bulk Data kit's export).
 */
function households(fixture) {
  return [
    { member: fixture.member, coverage: fixture.coverage, claims: fixture.claims },
    ...(fixture.additionalMembers ?? []),
  ];
}

async function seed(fixture, token) {
  for (const household of households(fixture)) {
    await seedHousehold(household, token);
  }

  console.log(`==> encounter-service ${SERVICES.encounter}`);
  // Encounter submission assigns a new id on every POST, so a re-run would
  // stack a second encounter on the member. Ask by control number first.
  for (const encounter of fixture.encounters) {
    await createUnlessPresent(
      encounter.encounterControlNumber,
      `${SERVICES.encounter}/api/encounters/control-number/${encounter.encounterControlNumber}`,
      `${SERVICES.encounter}/api/encounters`,
      token,
      asPayload(encounter));
  }

  await seedClaimsProviders(fixture.claimsProviders, token);
  await seedDirectory(fixture.directory, token);
}

/**
 * The practitioners the claims name by literal reference (the EOB's
 * careTeam), so a suite that follows the reference finds the Practitioner.
 */
async function seedClaimsProviders(claimsProviders, token) {
  console.log(`==> provider-service ${SERVICES.provider} (claims providers)`);
  for (const provider of claimsProviders.providers) {
    await createUnlessPresent(
      provider.npi,
      `${SERVICES.provider}/api/v1/providers/npi/${provider.npi}`,
      `${SERVICES.provider}/api/v1/providers`,
      token, asPayload(provider));
  }
}

async function seedHousehold({ member, coverage, claims }, token) {
  console.log(`==> member-service ${SERVICES.member} (${member.memberId})`);
  await put('member', 'POST', `${SERVICES.member}/api/v1/members`, token, asPayload(member));

  console.log(`==> coverage-service ${SERVICES.coverage}`);
  // coverage-service answers 201 to a second identical POST rather than 409, so
  // a re-run would stack duplicate coverage on the member. Ask first.
  const history = () => call(
    'GET', `${SERVICES.coverage}/api/v1/coverage/member/${coverage.memberId}/history`, token);
  const existing = await history();
  const alreadyCovered = existing.status === 200 && JSON.parse(existing.text).length > 0;
  if (alreadyCovered) {
    console.log('    coverage: already present');
  } else {
    await put('coverage', 'POST', `${SERVICES.coverage}/api/v1/coverage`, token, asPayload(coverage));
  }
  const coverageId = await seededCoverageId(await history(), coverage.memberId);

  console.log(`==> claims-service ${SERVICES.claims}`);
  for (const claim of claims) {
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
    // The claim names the coverage it was paid under, so its EOB's
    // insurance.coverage is a literal Coverage reference. The CARIN BB kit
    // reads that Coverage to compare its payor with the EOB's insurer, and an
    // EOB that names its coverage only by display gives it nothing to read.
    await put(claim.claimNumber, 'POST', `${SERVICES.claims}/api/v1/claims`, token,
      { ...asPayload(claim), coverageId });
  }
}

/** The id coverage-service gave the member's coverage, read back from its history. */
async function seededCoverageId(history, memberId) {
  if (history.status !== 200) {
    throw new Error(`coverage history for ${memberId} answered ${history.status}\n${history.text.slice(0, 2000)}`);
  }
  const [first] = JSON.parse(history.text);
  if (!first?.id) {
    throw new Error(`coverage history for ${memberId} returned no coverage with an id.`);
  }
  return first.id;
}

/**
 * Creates a record unless a read says it is already there. None of these
 * creates is idempotent on its own — a second network or plan POST with the
 * same id fails on the store's key, and a second provider POST is refused by
 * NPI — so each is asked about first, and an unexpected answer stops the seed.
 */
async function createUnlessPresent(label, existsUrl, createUrl, token, body) {
  const existing = await call('GET', existsUrl, token);
  if (existing.status === 200) {
    console.log(`    ${label}: already present`);
    return;
  }
  if (existing.status !== 404) {
    throw new Error(
      `${label}: the lookup answered ${existing.status}, so whether it is already seeded is ` +
      `unknown. Refusing to create rather than risk a duplicate.\n${existing.text.slice(0, 2000)}`);
  }
  await put(label, 'POST', createUrl, token, body);
}

/**
 * The public Plan-Net directory's sources (G-36): the network and providers in
 * provider-service, the plan and its document in benefit-plan-service.
 * fhir-service builds the published directory from these; nothing is written
 * to fhir-service itself.
 */
async function seedDirectory(directory, token) {
  console.log(`==> provider-service ${SERVICES.provider}`);
  const network = asPayload(directory.network);
  await createUnlessPresent(
    network.organizationId,
    `${SERVICES.provider}/api/v1/networks/${network.organizationId}`,
    `${SERVICES.provider}/api/v1/networks`,
    token, network);
  for (const provider of directory.providers) {
    await createUnlessPresent(
      provider.npi,
      `${SERVICES.provider}/api/v1/providers/npi/${provider.npi}`,
      `${SERVICES.provider}/api/v1/providers`,
      token, asPayload(provider));
  }

  console.log(`==> benefit-plan-service ${SERVICES.benefitPlan}`);
  const plan = asPayload(directory.plan);
  await createUnlessPresent(
    plan.planId,
    `${SERVICES.benefitPlan}/api/v1/plans/${plan.planId}`,
    `${SERVICES.benefitPlan}/api/v1/plans`,
    token, plan);
}

/**
 * Reads the seeded data back as the FHIR a suite would read, which is the only
 * check that matters: the services accepting a write does not mean the FHIR
 * path serves it, and it certainly does not mean it serves the right profile.
 */
async function verify(fixture, token) {
  for (const household of households(fixture)) {
    await verifyHousehold(household, token);
  }

  // The encounters as the US Core kit will find them: by patient, through
  // fhir-service. No profile is checked — the projection declares none
  // (EncounterFhirProjector) — only that each seeded encounter is served.
  const encounterUrl = `${SERVICES.fhir}/fhir/r4/Encounter?patient=${fixture.member.memberId}`;
  console.log(`==> verifying the encounters ${encounterUrl}`);
  const encounters = await readFhir(encounterUrl, token, 'the Encounter search');
  const identifiers = (encounters.entry ?? [])
    .flatMap((e) => e.resource?.identifier ?? [])
    .map((i) => i.value);
  for (const encounter of fixture.encounters) {
    if (!identifiers.includes(encounter.encounterControlNumber)) {
      throw new Error(
        `${encounter.encounterControlNumber} was seeded but the Encounter search did not return it ` +
        `(total ${encounters.total ?? 0}).`);
    }
    console.log(`    Encounter ${encounter.encounterControlNumber}`);
  }

  for (const provider of fixture.claimsProviders.providers) {
    const practitionerUrl = `${SERVICES.fhir}/fhir/r4/Practitioner/${provider.npi}`;
    console.log(`==> verifying the claims' practitioner ${practitionerUrl}`);
    const practitioner = await readFhir(practitionerUrl, token, `the claims' practitioner ${provider.npi}`);
    expectProfile(
      practitioner, 'http://hl7.org/fhir/us/carin-bb/StructureDefinition/C4BB-Practitioner', 'Practitioner');
  }

  await verifyDirectory(fixture.directory);
}

async function verifyHousehold({ member, claims }, token) {
  const patientUrl = `${SERVICES.fhir}/fhir/r4/Patient/${member.memberId}`;
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
    `${SERVICES.claims}/fhir/ProviderAccess/ExplanationOfBenefit?patient=${member.memberId}`;
  console.log(`==> verifying the Provider Access EOBs ${eobUrl}`);
  const bundle = await readFhir(eobUrl, token, 'the Provider Access EOB search');
  const served = (bundle.entry ?? []).flatMap((e) => e.resource?.meta?.profile ?? []);

  for (const claim of claims) {
    if (!claim.expectProfile) continue;
    if (!served.includes(claim.expectProfile)) {
      throw new Error(
        `${claim.claimNumber} should project to ${claim.expectProfile}, but the search ` +
        `returned ${served.length ? served.join(', ') : 'nothing'}.`);
    }
    console.log(`    ${claim.claimNumber} — ${claim.expectProfile.split('/').pop()}`);
  }
}

/**
 * Reads the public directory back the way the Plan-Net kit will: with no
 * token, through fhir-service. Every Plan-Net resource type must have at least
 * one instance, because the kit finds what it tests with parameterless
 * searches and reports a type with none as untested rather than as CHO's
 * failure — which would read as a pass that proved nothing.
 */
async function verifyDirectory(directory) {
  const base = `${SERVICES.fhir}/fhir/r4/directory/${directory.directoryId}`;
  console.log(`==> verifying the public directory ${base} (no token)`);
  const checks = [
    ['Organization', 'type=ntwk', 'plannet-Network'],
    ['Organization', 'type=prvgrp,fac,atyprv,payer', 'plannet-Organization'],
    ['Practitioner', '', 'plannet-Practitioner'],
    ['PractitionerRole', '', 'plannet-PractitionerRole'],
    ['Location', '', 'plannet-Location'],
    ['HealthcareService', '', 'plannet-HealthcareService'],
    ['OrganizationAffiliation', '', 'plannet-OrganizationAffiliation'],
    ['InsurancePlan', '', 'plannet-InsurancePlan'],
    ['Endpoint', '', 'plannet-Endpoint'],
  ];
  for (const [type, query, profile] of checks) {
    const url = `${base}/${type}?${query ? `${query}&` : ''}_count=1`;
    const { status, text } = await call('GET', url, null, undefined, 'application/fhir+json');
    if (status !== 200) {
      throw new Error(`the public directory answered ${status} to ${url}.\n${text.slice(0, 2000)}`);
    }
    const bundle = JSON.parse(text);
    const first = bundle.entry?.[0]?.resource;
    if (!first) {
      throw new Error(`the public directory has no ${profile} (${url} returned total ${bundle.total ?? 0}).`);
    }
    expectProfile(
      first, `http://hl7.org/fhir/us/davinci-pdex-plan-net/StructureDefinition/${profile}`, type);
    console.log(`    ${type}/${first.id} — ${profile} (total ${bundle.total})`);
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

const token = devUserToken(fixture.auth, fixture.tenantId);

if (!process.argv.includes('--verify-only')) {
  await seed(fixture, token);
}
await verify(fixture, token);
console.log('==> seeded');
