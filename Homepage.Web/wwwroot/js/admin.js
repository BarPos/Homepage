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
  // A single button with data-confirm (e.g. "Delete" next to "Mark as read" in one form) asks only for itself.
  document.addEventListener("click", (e) => {
    const btn = e.target.closest && e.target.closest("button[data-confirm]");
    if (!btn || !btn.form) return;
    if (btn.dataset.confirmed === "1") { delete btn.dataset.confirmed; return; }
    e.preventDefault();
    hpConfirm({
      title: btn.dataset.confirmTitle || "Are you sure?",
      message: btn.dataset.confirm,
      button: btn.dataset.confirmButton || "Continue",
      danger: "confirmDanger" in btn.dataset,
    }).then((ok) => {
      if (!ok) return;
      btn.dataset.confirmed = "1";
      btn.click();
    });
  });
  document.addEventListener("change", (e) => {
    if (e.target.matches && e.target.matches("select[data-autosubmit]")) e.target.form.submit();
  });
  document.addEventListener("hp-flash", (e) => hpFlash(e.detail.value || e.detail.message, e.detail.kind));

  // Analytics: "Don't count my visits" sets a flag the public tracking script reads (same origin, this browser only).
  const ignoreButtons = document.querySelectorAll("[data-hp-ignore-toggle]");
  if (ignoreButtons.length) {
    const read = () => { try { return localStorage.getItem("hp_analytics_ignore") === "1"; } catch (e) { return false; } };
    const show = () => {
      const on = read();
      document.querySelectorAll("[data-hp-ignore-state]").forEach((el) => { el.textContent = on ? "not counted" : "counted"; });
      ignoreButtons.forEach((b) => { b.textContent = on ? "Count my visits again" : "Don't count my visits"; });
    };
    ignoreButtons.forEach((b) => b.addEventListener("click", () => {
      try { if (read()) localStorage.removeItem("hp_analytics_ignore"); else localStorage.setItem("hp_analytics_ignore", "1"); } catch (e) { hpFlash("This browser blocks local storage.", "error"); }
      show();
    }));
    show();
  }

  // Link chooser: a dialog of pages, blogs and posts; choosing one puts its address in the field.
  let linkTarget = null;
  document.addEventListener("click", (e) => {
    const opener = e.target.closest && e.target.closest("[data-link-for]");
    if (opener) {
      linkTarget = opener.dataset.linkFor;
      if (linkTarget !== "@markdown") document.querySelectorAll("[data-link-external]").forEach((el) => { el.hidden = true; });
      return;
    }
    const add = e.target.closest && e.target.closest("[data-link-external-add]");
    if (add && linkTarget === "@markdown" && typeof window.hpLinkPick === "function") {
      const dlg = add.closest("dialog");
      const url = (dlg.querySelector("[data-link-external-url]").value || "").trim();
      if (!/^(https?:\/\/|mailto:|tel:|\/|#)/i.test(url)) { hpFlash("Enter an address that starts with https://, /, #, mailto: or tel:.", "error"); return; }
      const text = (dlg.querySelector("[data-link-external-text]").value || "").trim();
      const newTab = dlg.querySelector("[data-link-external-newtab]").checked;
      dlg.close();
      window.hpLinkPick(url, text, newTab);
      return;
    }
    const choice = e.target.closest && e.target.closest("[data-link-value]");
    if (!choice) return;
    const value = choice.dataset.linkValue, label = "→ " + choice.dataset.linkLabel;
    // The Markdown editor asks for a link through a callback instead of a field.
    if (linkTarget === "@markdown" && typeof window.hpLinkPick === "function") {
      const dlg = choice.closest("dialog");
      const text = (dlg.querySelector("[data-link-external-text]")?.value || "").trim();
      const newTab = !!dlg.querySelector("[data-link-external-newtab]")?.checked;
      dlg.close();
      window.hpLinkPick(value, text || choice.dataset.linkTitle || "", newTab);
      return;
    }
    const input = linkTarget && document.getElementById(linkTarget);
    if (!input) return;
    input.value = value;
    const shown = document.querySelector('[data-link-resolved="' + linkTarget + '"]');
    if (shown) shown.textContent = choice.dataset.linkLabel;
    input.closest(".link-field")?.classList.add("has-ref");
    const chip = input.closest(".link-field")?.querySelector(".link-chip");
    chip?.classList.remove("missing");
    chip?.classList.toggle("draft", (choice.dataset.linkLabel || "").includes("draft:"));
    window.dispatchEvent(new CustomEvent("hp-link", { detail: { id: linkTarget, value, label } }));
    input.dispatchEvent(new Event("input", { bubbles: true }));
    input.dispatchEvent(new Event("change", { bubbles: true }));
    choice.closest("dialog")?.close();
    input.focus();
  });

  // ✕ on a chosen link: back to an empty address field.
  document.addEventListener("click", (e) => {
    const clear = e.target.closest && e.target.closest("[data-link-clear]");
    if (!clear) return;
    const input = document.getElementById(clear.dataset.linkClear);
    if (!input) return;
    input.value = "";
    input.closest(".link-field")?.classList.remove("has-ref");
    input.dispatchEvent(new Event("input", { bubbles: true }));
    input.dispatchEvent(new Event("change", { bubbles: true }));
    input.focus();
  });

  // Icon picker (inspector): a dialog of icons; choosing one fills the field and saves the block.
  let iconTarget = null;
  document.addEventListener("click", (e) => {
    const opener = e.target.closest && e.target.closest("[data-icon-for]");
    if (opener) { iconTarget = opener.dataset.iconFor; return; }
    const clear = e.target.closest && e.target.closest("[data-icon-clear]");
    const choice = e.target.closest && e.target.closest("[data-icon-value]");
    if (!clear && !choice) return;
    const id = clear ? clear.dataset.iconClear : iconTarget;
    const input = id && document.getElementById(id);
    if (!input) return;
    input.value = clear ? "" : choice.dataset.iconValue;
    const preview = document.querySelector('[data-icon-preview="' + id + '"]');
    if (preview) preview.innerHTML = clear ? '<span class="muted">—</span>' : choice.innerHTML;
    input.dispatchEvent(new CustomEvent("hp-icon", { bubbles: true, detail: { value: input.value, svg: clear ? "" : choice.innerHTML } }));
    input.dispatchEvent(new Event("input", { bubbles: true }));
    input.dispatchEvent(new Event("change", { bubbles: true }));
    if (choice) choice.closest("dialog")?.close();
  });

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
