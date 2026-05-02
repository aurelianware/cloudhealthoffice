/**
 * Module augmentation for @types/fhir v0.0.42.
 *
 * The base `Resource` interface in that version omits `resourceType` — it is
 * only declared on each concrete resource subtype (e.g. `Patient`, `Claim`).
 * Every FHIR resource carries the field at runtime, so we add it here so that
 * code which receives a `Resource`-typed value can safely read the discriminator
 * without a per-callsite cast.
 */

// `export {}` makes this file a module so that the `declare module` block below
// is treated as an augmentation of the existing `fhir/r4` definitions rather
// than a replacement of them.
export {};

declare module 'fhir/r4' {
  interface Resource {
    /** FHIR resource-type discriminator, present on every resource at runtime. */
    resourceType?: string;
  }
}
