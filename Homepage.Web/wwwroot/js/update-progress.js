// "Update now": stage the update, then follow it through the restart and reload when the new version answers.
// The server goes away while update.sh backs up the database and switches versions, so every step is inferred from
// polling /admin/updates/status: unreachable = restarting; answering with the new version = done; answering with the
// old version and a RolledBack/Failed run = rolled back.
(function () {
  const form = document.querySelector("form[data-update-form]");
  if (!form) return;

  const POLL_MS = 2000;
  const STUCK_AFTER_MS = 120000;   // the updater has not stopped the server after 2 minutes
  const SLOW_AFTER_MS = 15 * 60000;

  const steps = [
    ["download", "Downloading and checking the release"],
    ["updater", "Waiting for the updater"],
    ["restart", "Backing up the database and restarting"],
    ["start", "Starting the new version"],
  ];

  function buildDialog(from, to) {
    const dlg = document.createElement("dialog");
    dlg.className = "modal update-modal";
    dlg.addEventListener("cancel", (e) => e.preventDefault()); // Esc must not hide a running update
    dlg.innerHTML = `
      <h2>Updating to ${to}</h2>
      <p class="update-sub">From ${from}. Keep this page open; it reloads by itself when the update is done.</p>
      <ol class="update-steps">${steps.map(([k, label]) => `<li data-step="${k}"><span class="dot"></span>${label}</li>`).join("")}</ol>
      <p class="update-note" hidden></p>
      <div class="modal-actions"><span class="update-time muted small"></span><span style="flex:1"></span>
        <button type="button" class="update-close" hidden>Close</button></div>`;
    document.body.appendChild(dlg);
    dlg.showModal();
    return dlg;
  }

  form.addEventListener("submit", async (e) => {
    e.preventDefault();
    const from = form.dataset.from, to = form.dataset.to;
    const dlg = buildDialog(from, to);
    const started = Date.now();
    const $ = (sel) => dlg.querySelector(sel);
    const set = (key, state) => { const li = $(`[data-step="${key}"]`); if (li) li.dataset.state = state; };
    const note = (text, kind) => { const n = $(".update-note"); n.hidden = !text; n.textContent = text || ""; n.className = "update-note " + (kind || ""); };
    const finish = (ok, text) => {
      note(text, ok ? "ok" : "error");
      const btn = $(".update-close");
      btn.hidden = false;
      btn.textContent = ok ? "Reload now" : "Close";
      btn.onclick = () => location.assign("/admin/settings");
      // The reloaded Settings page shows the finished run ("Last update: … Succeeded"); no text travels in the URL.
      if (ok) setTimeout(() => location.assign("/admin/settings"), 1500);
    };
    const tick = setInterval(() => {
      const s = Math.round((Date.now() - started) / 1000);
      $(".update-time").textContent = `${Math.floor(s / 60)}:${String(s % 60).padStart(2, "0")}`;
    }, 1000);

    // 1. stage
    set("download", "active");
    let staged;
    try {
      const body = new URLSearchParams(new FormData(form));
      const res = await fetch(form.action, {
        method: "POST", body,
        headers: { "Accept": "application/json", "RequestVerificationToken": window.hpCsrf ? hpCsrf() : "" },
      });
      staged = await res.json().catch(() => ({}));
      if (!res.ok) throw new Error(staged.error || `The update could not be started (${res.status}).`);
    } catch (err) {
      set("download", "failed");
      clearInterval(tick);
      return finish(false, err.message);
    }
    set("download", "done");
    set("updater", "active");

    // 2. follow it through the restart
    let wentDown = false;
    for (;;) {
      await new Promise((r) => setTimeout(r, POLL_MS));
      const elapsed = Date.now() - started;
      let res = null, data = null;
      try {
        res = await fetch("/admin/updates/status", { cache: "no-store", redirect: "manual", headers: { "Accept": "application/json" } });
        if (res.ok) data = await res.json();
      } catch { /* the server is restarting */ }

      if (res && res.type === "opaqueredirect") { // signed out by the restart: sign in, then land on Settings
        clearInterval(tick);
        location.assign("/admin/login?returnUrl=" + encodeURIComponent("/admin/settings"));
        return;
      }
      if (!data) {
        wentDown = true;
        set("updater", "done");
        set("restart", "active");
        note(elapsed > SLOW_AFTER_MS ? "This is taking longer than usual. On the server: journalctl -u homepage-update" : "");
        continue;
      }
      if (wentDown) set("restart", "done"); // it answers again, so the restart is over
      if (data.version === to) {
        ["updater", "restart", "start"].forEach((k) => set(k, "done"));
        clearInterval(tick);
        return finish(true, `Homepage ${to} is running. Reloading…`);
      }
      const run = data.run;
      if (run && run.to === to && (run.state === "RolledBack" || run.state === "Failed")) {
        set(wentDown ? "start" : "updater", "failed");
        clearInterval(tick);
        return finish(false, (run.state === "RolledBack" ? `The update was rolled back; ${data.version} is running again. ` : "The update failed. ")
          + (run.message || "") + " Details: journalctl -u homepage-update");
      }
      if (wentDown) { set("start", "active"); continue; } // back, but still the old version: rolling back or finishing
      if (elapsed > STUCK_AFTER_MS) {
        note("The updater has not started yet. On the server, check: systemctl status homepage-update.path, and journalctl -u homepage-update.", "error");
      }
    }
  });
})();
