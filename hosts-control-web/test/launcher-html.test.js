const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

test("all inline scripts in the Launcher HTML parse as JavaScript", () => {
  const htmlPath = path.join(__dirname, "..", "..", "wwwroot", "index.html");
  const html = fs.readFileSync(htmlPath, "utf8");
  const scripts = [...html.matchAll(/<script\\b[^>]*>([\\s\\S]*?)<\\/script>/gi)];
  assert.ok(scripts.length > 0, "expected at least one inline script in wwwroot/index.html");
  scripts.forEach((match, index) => {
    assert.doesNotThrow(() => new vm.Script(match[1], { filename: `wwwroot/index.html<script ${index + 1}>` }));
  });
});
