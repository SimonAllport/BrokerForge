// The review page's markup (plan 5, found while recording): on a new connection the page has no review yet, and its
// "Save and read the definition" button, which saves the row first, must still be there to press.
const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");

const html = fs.readFileSync(path.join(__dirname, "../../src/BrokerForge.WebResources/review.html"), "utf8");

test("the read button is outside the part hidden until a review exists", () => {
  const hidden = html.indexOf('<div id="page" hidden>');
  assert.ok(hidden > 0, "review.html has the hidden #page section");
  assert.ok(html.indexOf('id="read"') >= 0 && html.indexOf('id="read"') < hidden, "#read comes before #page, not inside it");
});
