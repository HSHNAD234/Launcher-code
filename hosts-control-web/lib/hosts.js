const DOMAINS = Object.freeze([
  "api.homesweethomegame.com",
  "hshsapi.homesweethomegame.com",
  "logapi.homesweethomegame.com",
  "boatltpk"
]);
function validateRules(input) {
  if (!Array.isArray(input) || input.length !== DOMAINS.length) throw new Error("Submit exactly four domain records.");
  const seen = new Set();
  const normalized = input.map(raw => {
    if (!raw || typeof raw !== "object" || Array.isArray(raw)) throw new Error("Each record must be an object.");
    const domain = String(raw.domain || "").trim().toLowerCase();
    if (!DOMAINS.includes(domain)) throw new Error("Unsupported domain: " + domain);
    if (seen.has(domain)) throw new Error("Duplicate domain: " + domain);
    seen.add(domain);
    const enabled = raw.enabled === true;
    const purpose = String(raw.purpose || "").trim();
    const note = String(raw.note || "").trim();
    if (purpose.length > 120) throw new Error("Purpose must be 120 characters or fewer.");
    if (note.length > 160) throw new Error("Notes must be 160 characters or fewer.");
    return { domain, enabled, purpose, note };
  });
  if (seen.size !== DOMAINS.length) throw new Error("All managed domains are required.");
  return DOMAINS.map(domain => normalized.find(item => item.domain === domain));
}
module.exports = { DOMAINS, validateRules };
