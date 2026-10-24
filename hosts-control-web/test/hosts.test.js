const test = require("node:test");
const assert = require("node:assert/strict");
const { DOMAINS, validateRules } = require("../lib/hosts");
const rules = (overrides = {}) => DOMAINS.map(domain => ({
  domain, enabled: false, purpose: "", note: "", ...(overrides[domain] || {})
}));
test("allows only the four configured domains", () => {
  assert.equal(DOMAINS.length, 4);
  assert.deepEqual(validateRules(rules()).map(x => x.domain), DOMAINS);
});
test("rejects a domain outside the fixed registry", () => {
  const input = rules(); input[0].domain = "example.com";
  assert.throws(() => validateRules(input), /Unsupported domain/);
});
test("rejects duplicate domains", () => {
  const input = rules(); input[1].domain = input[0].domain;
  assert.throws(() => validateRules(input), /Duplicate domain/);
});
test("validates metadata lengths", () => {
  const input = rules(); input[0].note = "x".repeat(161);
  assert.throws(() => validateRules(input), /160 characters/);
});
test("returns domains in stable order", () => {
  assert.deepEqual(validateRules(rules().reverse()).map(x => x.domain), DOMAINS);
});
