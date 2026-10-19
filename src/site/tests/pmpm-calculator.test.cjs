const { test } = require("node:test");
const assert = require("node:assert/strict");
const { calculate } = require("../js/pmpm-calculator.js");
function scenario(overrides = {}) {
  return Object.assign(
    {
      memberMonths: 1200000,
      horizon: 3,
      licenseKnown: true,
      supportKnown: true,
      currentPmpm: 2,
      alternativePmpm: 1.5,
      currentFixed: 100000,
      alternativeFixed: 100000,
      currentInfrastructure: 0,
      alternativeInfrastructure: 0,
      currentSupport: 0,
      alternativeSupport: 0,
      currentStaffing: 0,
      alternativeStaffing: 0,
      currentIntegration: 0,
      alternativeIntegration: 0,
      transition: 1800000,
      exit: 200000,
      parallel: 100000,
    },
    overrides,
  );
}
test("lower PMPM can cost more across the horizon, counting transition and parallel once", () => {
  const x = calculate(scenario());
  assert.equal(x.currentAnnual, 2500000);
  assert.equal(x.alternativeAnnual, 1900000);
  assert.equal(x.currentHorizon, 7500000);
  assert.equal(x.alternativeHorizon, 7800000);
  assert.equal(x.difference, -300000);
  assert.equal(x.payback, 3.5);
  assert.equal(x.effectivePmpm, 7800000 / 3600000);
});
test("annual categories are counted once per year", () => {
  const x = calculate(
    scenario({
      currentInfrastructure: 10,
      currentSupport: 20,
      currentStaffing: 30,
      currentIntegration: 40,
      alternativeInfrastructure: 100,
      alternativeSupport: 200,
      alternativeStaffing: 300,
      alternativeIntegration: 400,
    }),
  );
  assert.equal(x.currentAnnual, 2500100);
  assert.equal(x.alternativeAnnual, 1901000);
});
test("unknown commercial assumptions withhold totals", () => {
  for (const patch of [
    { licenseKnown: false },
    { supportKnown: false },
    { licenseKnown: undefined },
  ])
    assert.deepEqual(calculate(scenario(patch)), { unknown: true });
});
test("rejects blank, negative, nonnumeric, infinite costs and numeric overflow", () => {
  for (const value of [
    "",
    " ",
    null,
    undefined,
    -1,
    NaN,
    Infinity,
    "abc",
    true,
  ])
    assert.throws(() => calculate(scenario({ alternativePmpm: value })));
  assert.throws(() =>
    calculate(scenario({ alternativePmpm: Number.MAX_VALUE })),
  );
});
test("validates positive whole member months and bounded whole years", () => {
  for (const value of [0, -1, 1.5, ""])
    assert.throws(() => calculate(scenario({ memberMonths: value })));
  for (const value of [0, 51, 2.5, ""])
    assert.throws(() => calculate(scenario({ horizon: value })));
});
test("no payback when recurring difference is zero or negative", () => {
  assert.equal(calculate(scenario({ alternativePmpm: 2 })).payback, null);
  assert.equal(calculate(scenario({ alternativePmpm: 3 })).payback, null);
});
test("explicit zero one-time cost gives immediate payback; positive difference is retained", () => {
  const x = calculate(scenario({ transition: 0, exit: 0, parallel: 0 }));
  assert.equal(x.payback, 0);
  assert.equal(x.difference, 1800000);
});
