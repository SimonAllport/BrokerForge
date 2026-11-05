// BrokerForge review page logic (spec 5, screens 2 and 3): pure functions, no DOM and no Xrm, so node can test them.
// Loaded in the browser as the web resource bf_/brokerforge/review-model.js, where it sets window.BrokerForgeReview.
(function (root, factory) {
  if (typeof module === "object" && module.exports) module.exports = factory();
  else root.BrokerForgeReview = factory();
})(typeof self !== "undefined" ? self : this, function () {
  "use strict";

  // A job still Running this long after it started has stopped without a report: the host's limit is 15 minutes and the
  // lease lasts 20 (HLD D-36), so by then another Generate can start.
  var STALE_MINUTES = 20;

  function result(row) {
    try {
      return row && row.bf_generationresult ? JSON.parse(row.bf_generationresult) : null;
    } catch (e) {
      return null;
    }
  }

  // Generate is enabled only when every ticked endpoint is Ready (spec 5): the review is ready and the change list has
  // nothing refused or blocked. A missing key does not block it; the page shows "needs key" beside it.
  function canGenerate(read) {
    if (!read || read.readOnly || read.status !== "Ready" || !read.preview || read.preview.error) return false;
    return (read.preview.blocked || []).length === 0 && (read.preview.refused || []).length === 0;
  }

  function missingKeys(read) {
    var secrets = (read && read.environment && read.environment.secrets) || [];
    return secrets.filter(function (s) { return !s.set; }).map(function (s) { return s.name; });
  }

  // The answers to save: the saved ones with the page's edits applied. An empty edit removes the answer. Keys are sorted
  // so the same answers always save as the same text.
  function answersFrom(saved, edits) {
    var merged = {};
    Object.keys(saved || {}).forEach(function (k) { merged[k] = saved[k]; });
    Object.keys(edits || {}).forEach(function (k) {
      if (edits[k] === "" || edits[k] === null || edits[k] === undefined) delete merged[k];
      else merged[k] = String(edits[k]);
    });
    var sorted = {};
    Object.keys(merged).sort().forEach(function (k) { sorted[k] = merged[k]; });
    return sorted;
  }

  function isRunning(row) {
    return !!row && (row.bf_generationstate === "Queued" || row.bf_generationstate === "Running");
  }

  function statusLine(row, now) {
    var state = row && row.bf_generationstate;
    var r = result(row);
    if (!state) return "Not generated yet.";
    if (state === "Queued" || state === "Running") {
      var started = row.bf_generationstarted ? new Date(row.bf_generationstarted) : null;
      if (started && (now - started) / 60000 > STALE_MINUTES)
        return "The last Generate stopped without a report; Generate again.";
      return "Generating" + (started ? " (started " + started.toISOString().slice(11, 16) + " UTC)" : "") + "…";
    }
    if (state === "Succeeded")
      return "Generated" + (r && r.designVersion ? " (design v" + r.designVersion + ")" : "") + (needsRole(row) ? ". The role still needs an administrator." : ".");
    if (r && r.needsConfirmation)
      return "Nothing was changed: the changes changed since you reviewed them. Read again and confirm the new list.";
    return "Generate failed: " + ((r && r.failure) || "no reason was reported");
  }

  // A succeeded job that left the role to an administrator (HLD D-36).
  function needsRole(row) {
    var r = result(row);
    return !!row && row.bf_generationstate === "Succeeded" && !!r && (r.pending || []).length > 0;
  }

  // Screen 3's links.
  function doneLinks(orgUrl, table) {
    var org = String(orgUrl).replace(/\/+$/, "");
    return {
      open: org + "/main.aspx?pagetype=entitylist&etn=" + encodeURIComponent(table),
      addToApp: "https://make.powerapps.com/",
    };
  }

  return { canGenerate: canGenerate, missingKeys: missingKeys, answersFrom: answersFrom, isRunning: isRunning, statusLine: statusLine,
    needsRole: needsRole, doneLinks: doneLinks, STALE_MINUTES: STALE_MINUTES };
});
