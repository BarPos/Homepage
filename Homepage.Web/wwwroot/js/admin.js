// Admin helpers: send the antiforgery token with every htmx request, and show server errors in the flash area.
(function () {
  const token = () => document.querySelector('meta[name="csrf-token"]')?.content || "";
  window.hpCsrf = token;
  document.addEventListener("htmx:configRequest", (e) => {
    e.detail.headers["RequestVerificationToken"] = token();
  });
  document.addEventListener("htmx:responseError", (e) => {
    const xhr = e.detail.xhr;
    if (xhr.getResponseHeader("HX-Retarget")) return; // the server already sent a flash fragment
    hpFlash(xhr.responseText && xhr.responseText.length < 500 ? xhr.responseText : "The request failed (" + xhr.status + ").", "error");
  });
  window.hpFlash = function (message, kind) {
    const el = document.getElementById("flash");
    if (!el) return;
    const div = document.createElement("div");
    div.className = "alert " + (kind === "error" ? "alert-error" : "alert-ok");
    div.textContent = message;
    el.replaceChildren(div);
    clearTimeout(window.hpFlashTimer);
    window.hpFlashTimer = setTimeout(() => div.remove(), kind === "error" ? 8000 : 3000);
  };
  // No inline handlers (the admin CSP forbids them): confirmations and auto-submitting selects.
  document.addEventListener("submit", (e) => {
    const msg = e.target.dataset && e.target.dataset.confirm;
    if (msg && !confirm(msg)) e.preventDefault();
  }, true);
  document.addEventListener("change", (e) => {
    if (e.target.matches && e.target.matches("select[data-autosubmit]")) e.target.form.submit();
  });
  document.addEventListener("hp-flash", (e) => hpFlash(e.detail.value || e.detail.message, e.detail.kind));
})();
