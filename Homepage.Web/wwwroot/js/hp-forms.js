// Public forms (Form and Newsletter sign-up blocks): send in place and show the answer, without leaving the page.
// Without this script the forms post normally and the server answers with a page.
(() => {
  if (window.hpForms) return;
  window.hpForms = true;

  // Pages are cached for a while, so the anti-spam token in the HTML may be old: get a fresh one when someone starts filling in.
  const fresh = async form => {
    if (form.dataset.fresh) return;
    form.dataset.fresh = '1';
    try {
      const r = await fetch('/hp/forms/token?scope=' + encodeURIComponent(form.dataset.scope), { cache: 'no-store' });
      if (r.ok) form.querySelector('input[name="_hp_t"]').value = (await r.json()).token;
    } catch { /* keep the one from the page */ }
  };

  const status = (form, text, ok) => {
    const s = form.querySelector('.hp-form-status');
    s.textContent = text;
    s.hidden = !text;
    s.classList.toggle('is-ok', !!ok);
    s.classList.toggle('is-error', !ok);
  };

  const clearErrors = form => form.querySelectorAll('.hp-field').forEach(f => {
    f.classList.remove('has-error');
    const e = f.querySelector('.hp-error'); if (e) { e.textContent = ''; e.hidden = true; }
  });

  document.addEventListener('focusin', e => { const f = e.target.closest?.('form[data-hp-form]'); if (f) fresh(f); });

  document.addEventListener('submit', async e => {
    const form = e.target;
    if (!form.matches?.('form[data-hp-form]')) return;
    e.preventDefault();
    const button = form.querySelector('[type="submit"]');
    if (button.disabled) return;
    button.disabled = true;
    form.classList.add('is-sending');
    clearErrors(form);
    try {
      const r = await fetch(form.action, { method: 'POST', body: new URLSearchParams(new FormData(form)), headers: { Accept: 'application/json' } });
      const a = await r.json().catch(() => null);
      if (!a) { status(form, r.status === 429 ? 'Too many tries. Please wait a minute and send it again.' : 'Something went wrong. Please try again.', false); return; }
      if (a.ok && a.redirect) { location.href = a.redirect; return; }
      if (a.ok) {
        const done = document.createElement('div');
        done.className = 'hp-form-done';
        done.setAttribute('role', 'status');
        done.textContent = a.message;
        form.replaceWith(done);
        return;
      }
      status(form, a.message, false);
      let first = null;
      for (const [key, msg] of Object.entries(a.errors || {})) {
        const f = form.querySelector(`.hp-field[data-field="${CSS.escape(key)}"]`);
        if (!f) continue;
        f.classList.add('has-error');
        const el = f.querySelector('.hp-error');
        if (el) { el.textContent = msg; el.hidden = false; }
        first ??= f.querySelector('input,textarea,select');
      }
      (first || form.querySelector('.hp-form-status')).focus?.();
    } catch {
      status(form, 'Could not send. Check your connection and try again.', false);
    } finally {
      button.disabled = false;
      form.classList.remove('is-sending');
    }
  });
})();
