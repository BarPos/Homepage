// Homepage analytics: one page view per page load, and how long the page was visible. No cookies, no storage
// (except an opt-out flag set from the admin), nothing from the page's content. Skipped in frames and for automation.
(function () {
  "use strict";
  if (window.top !== window || navigator.webdriver) return;
  try { if (localStorage.getItem("hp_analytics_ignore") === "1") return; } catch (e) { /* storage blocked: count */ }
  var key = Math.random().toString(36).slice(2, 10) + Date.now().toString(36);
  var url = location.pathname + location.search;
  function send(data) {
    var body = JSON.stringify(data);
    try {
      if (navigator.sendBeacon && navigator.sendBeacon("/hp/event", new Blob([body], { type: "text/plain" }))) return;
      fetch("/hp/event", { method: "POST", body: body, keepalive: true, credentials: "omit", headers: { "Content-Type": "text/plain" } });
    } catch (e) { /* never break the page */ }
  }
  send({ t: "v", u: url, r: document.referrer || null, w: window.innerWidth || 0, k: key });

  // Engagement: seconds the page was visible, sent (as a running total) whenever it is hidden or left.
  var visible = 0, since = document.visibilityState === "visible" ? Date.now() : 0, sent = 0;
  function flush() {
    if (since) { visible += Date.now() - since; since = 0; }
    var s = Math.round(visible / 1000);
    if (s > sent) { sent = s; send({ t: "e", u: url, k: key, s: s }); }
  }
  document.addEventListener("visibilitychange", function () {
    if (document.visibilityState === "hidden") flush(); else if (!since) since = Date.now();
  });
  window.addEventListener("pagehide", flush);
})();
