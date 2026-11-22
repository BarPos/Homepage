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
  /** A modal dialog. Resolves true when confirmed, false on Cancel, Esc or a click outside. */
  window.hpConfirm = function ({ title, message, button = "Continue", cancel = "Cancel", danger = false }) {
    return new Promise((resolve) => {
      const dlg = document.createElement("dialog");
      dlg.className = "modal";
      dlg.setAttribute("aria-labelledby", "hp-modal-title");
      const h = document.createElement("h2");
      h.id = "hp-modal-title";
      h.textContent = title;
      const p = document.createElement("p");
      p.textContent = message;
      const actions = document.createElement("div");
      actions.className = "modal-actions";
      const no = document.createElement("button");
      no.type = "button";
      no.textContent = cancel;
      const yes = document.createElement("button");
      yes.type = "button";
      yes.textContent = button;
      yes.className = danger ? "btn-danger-solid" : "btn-primary";
      actions.append(no, yes);
      dlg.append(h, p, actions);
      document.body.appendChild(dlg);
      let result = false;
      const close = (value) => { result = value; dlg.close(); };
      no.addEventListener("click", () => close(false));
      yes.addEventListener("click", () => close(true));
      dlg.addEventListener("click", (ev) => { if (ev.target === dlg) close(false); }); // backdrop
      dlg.addEventListener("close", () => { dlg.remove(); resolve(result); });
      dlg.showModal();
      (danger ? no : yes).focus(); // destructive actions start on Cancel
    });
  };

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
  // Forms with data-confirm ask first, in an in-page dialog (not the browser's confirm()).
  //   data-confirm="message"  data-confirm-title="…"  data-confirm-button="Delete"  data-confirm-danger
  document.addEventListener("submit", (e) => {
    const form = e.target;
    const msg = form.dataset && form.dataset.confirm;
    if (!msg) return;
    if (form.dataset.confirmed === "1") { delete form.dataset.confirmed; return; }
    e.preventDefault();
    e.stopImmediatePropagation();
    hpConfirm({
      title: form.dataset.confirmTitle || "Are you sure?",
      message: msg,
      button: form.dataset.confirmButton || "Continue",
      danger: "confirmDanger" in form.dataset,
    }).then((ok) => {
      if (!ok) return;
      form.dataset.confirmed = "1";
      if (form.requestSubmit) form.requestSubmit(); else form.submit();
    });
  }, true);
  document.addEventListener("change", (e) => {
    if (e.target.matches && e.target.matches("select[data-autosubmit]")) e.target.form.submit();
  });
  document.addEventListener("hp-flash", (e) => hpFlash(e.detail.value || e.detail.message, e.detail.kind));

  // In-page dialogs: <button data-open-dialog="id" data-dialog-type="Post">; [data-close-dialog] or Esc or the backdrop close.
  document.addEventListener("click", (e) => {
    const opener = e.target.closest && e.target.closest("[data-open-dialog]");
    if (opener) {
      const dlg = document.getElementById(opener.dataset.openDialog);
      if (!dlg) return;
      e.preventDefault();
      const type = opener.dataset.dialogType;
      const select = type && dlg.querySelector('select[name="type"]');
      if (select) { select.value = type; select.dispatchEvent(new Event("change", { bubbles: true })); }
      dlg.showModal();
      dlg.querySelector("[autofocus], input:not([type=hidden])")?.focus();
      return;
    }
    if (e.target.closest && e.target.closest("[data-close-dialog]")) { e.target.closest("dialog")?.close(); return; }
    if (e.target.tagName === "DIALOG" && e.target.dataset.lightDismiss !== undefined) e.target.close();
  });
})();
