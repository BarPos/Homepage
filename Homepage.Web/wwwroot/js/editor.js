// Homepage visual editor. Plain HTTP only: every change is one POST; the server validates it, saves the draft and
// the editor re-fetches the rendered tree. No WebSocket, no SignalR.
(function () {
  const bar = document.querySelector("[data-editor]");
  if (!bar) return;
  const target = bar.dataset.target;
  const region = bar.dataset.region;
  const readonly = bar.dataset.readonly === "1";
  const base = "/admin/editor/" + target;
  const iframe = document.getElementById("canvas");
  const layersEl = document.getElementById("layers");
  const statusEl = document.getElementById("ed-status");
  let selected = null;
  let busy = Promise.resolve();

  const status = (s, title) => { statusEl.className = "status-dot " + (s || ""); statusEl.title = title || s || ""; };
  const csrf = () => (window.hpCsrf ? window.hpCsrf() : "");

  async function api(op) {
    status("saving", "Saving…");
    const res = await fetch(base + "/ops", {
      method: "POST",
      headers: { "Content-Type": "application/json", "RequestVerificationToken": csrf(), "Accept": "application/json" },
      body: JSON.stringify(op),
    });
    let data = {};
    try { data = await res.json(); } catch { /* empty */ }
    if (!res.ok) {
      status("error", data.error || "Error");
      window.hpFlash && hpFlash(data.error || ("The change was not saved (" + res.status + ")."), "error");
      await refresh();
      return null;
    }
    return data;
  }

  // Serialize refreshes and ops so the canvas never shows an older tree than the server has.
  function queue(fn) { busy = busy.then(fn, fn); return busy; }

  async function refresh() {
    const [tree, layers] = await Promise.all([
      fetch(base + "/tree", { headers: { "HX-Request": "true" } }).then((r) => r.text()),
      fetch(base + "/layers", { headers: { "HX-Request": "true" } }).then((r) => r.text()),
    ]);
    const doc = iframe.contentDocument;
    const root = doc && doc.querySelector('[data-region="' + region + '"] .hp-list[data-parent=""]');
    if (root) {
      const tmp = doc.createElement("div");
      tmp.innerHTML = tree;
      const fresh = tmp.querySelector('.hp-list[data-parent=""]');
      if (fresh) root.replaceWith(fresh);
    }
    layersEl.innerHTML = layers;
    initLayers();
    initCanvas();
    highlight();
    status("saved", "All changes saved");
  }

  function run(op, then) {
    return queue(async () => {
      const r = await api(op);
      if (r) {
        if (then) then(r);
        await refresh();
      }
    });
  }

  // ---------------------------------------------------------------- selection + inspector
  function select(id) {
    selected = id;
    highlight();
    if (id) htmx.ajax("GET", base + "/inspector/" + encodeURIComponent(id), { target: "#inspector", swap: "innerHTML" });
    else document.getElementById("inspector").innerHTML = '<div class="empty">Select a block to edit its settings.</div>';
  }

  function highlight() {
    const doc = iframe.contentDocument;
    if (doc) {
      doc.querySelectorAll(".hp-selected").forEach((e) => e.classList.remove("hp-selected"));
      if (selected) {
        const el = doc.querySelector('.hp-block[data-block-id="' + CSS.escape(selected) + '"]');
        if (el) el.classList.add("hp-selected");
      }
    }
    layersEl.querySelectorAll("li.selected").forEach((e) => e.classList.remove("selected"));
    if (selected) layersEl.querySelector('li[data-id="' + CSS.escape(selected) + '"]')?.classList.add("selected");
  }

  /** Where a block from the palette goes: into the selected container, else after the selected block, else at the end. */
  function insertPoint() {
    const li = selected && layersEl.querySelector('li[data-id="' + CSS.escape(selected) + '"]');
    if (!li) return { parentId: null, index: layersEl.querySelector('ul.layers')?.children.length || 0 };
    const own = li.querySelector(":scope > ul");
    if (own) return { parentId: li.dataset.id, index: own.children.length };
    const ul = li.parentElement;
    return { parentId: ul.dataset.parent || null, index: Array.from(ul.children).indexOf(li) + 1 };
  }

  function siblingMove(delta) {
    const li = selected && layersEl.querySelector('li[data-id="' + CSS.escape(selected) + '"]');
    if (!li) return;
    const ul = li.parentElement;
    const index = Array.from(ul.children).indexOf(li) + delta;
    if (index < 0 || index >= ul.children.length) return;
    run({ op: "move", id: selected, parentId: ul.dataset.parent || null, index });
  }

  document.getElementById("inspector").addEventListener("click", (e) => {
    const btn = e.target.closest("[data-op]");
    if (!btn || !selected) return;
    const op = btn.dataset.op;
    if (op === "up") siblingMove(-1);
    else if (op === "down") siblingMove(1);
    else if (op === "duplicate") run({ op: "duplicate", id: selected }, (r) => { selected = r.newId; select(r.newId); });
    else if (op === "delete") run({ op: "delete", id: selected }, () => select(null));
  });

  // The inspector form saves through htmx; the server answers with HX-Trigger: hp-changed.
  document.body.addEventListener("hp-changed", () => queue(refresh));

  document.addEventListener("keydown", (e) => {
    if (readonly || !selected) return;
    const tag = (e.target.tagName || "").toLowerCase();
    if (tag === "input" || tag === "textarea" || tag === "select" || e.target.isContentEditable) return;
    // Delete only, not Backspace: there is no undo yet.
    if (e.key === "Delete") { e.preventDefault(); run({ op: "delete", id: selected }, () => select(null)); }
  });

  // ---------------------------------------------------------------- palette
  document.querySelectorAll(".palette").forEach((ul) => {
    Sortable.create(ul, { group: { name: "layers", pull: "clone", put: false }, sort: false, animation: 120, forceFallback: true });
    ul.addEventListener("click", (e) => {
      const li = e.target.closest("li[data-type]");
      if (!li || readonly) return;
      const p = insertPoint();
      run({ op: "insert", type: li.dataset.type, parentId: p.parentId, index: p.index }, (r) => select(r.newId));
    });
  });

  // ---------------------------------------------------------------- layers tree (drag from palette, reorder, nest)
  function initLayers() {
    if (readonly) return;
    layersEl.querySelectorAll("ul[data-parent]").forEach((ul) => {
      Sortable.create(ul, {
        group: { name: "layers", pull: true, put: true },
        animation: 150,
        forceFallback: true,
        handle: ".row",
        fallbackOnBody: true,
        swapThreshold: 0.6,
        onAdd: (evt) => {
          const type = evt.item.dataset.type;
          const fromPalette = evt.from.classList.contains("palette");
          if (fromPalette) {
            evt.item.remove();
            run({ op: "insert", type, parentId: evt.to.dataset.parent || null, index: evt.newIndex }, (r) => select(r.newId));
          }
        },
        onEnd: (evt) => {
          if (evt.from.classList.contains("palette")) return;
          if (evt.from === evt.to && evt.oldIndex === evt.newIndex) return;
          run({ op: "move", id: evt.item.dataset.id, parentId: evt.to.dataset.parent || null, index: evt.newIndex });
        },
      });
    });
    layersEl.querySelectorAll("li[data-id] > .row").forEach((row) =>
      row.addEventListener("click", () => select(row.parentElement.dataset.id)));
  }

  // ---------------------------------------------------------------- canvas (click to select, drag to reorder)
  function initCanvas() {
    const doc = iframe.contentDocument;
    const win = iframe.contentWindow;
    if (!doc || !win) return;
    const regionEl = doc.querySelector('[data-region="' + region + '"]');
    if (!regionEl) return;
    if (!regionEl.dataset.hpBound) {
      regionEl.dataset.hpBound = "1";
      regionEl.addEventListener("click", (e) => {
        const block = e.target.closest(".hp-block");
        e.preventDefault();
        if (block) { e.stopPropagation(); select(block.dataset.blockId); }
      }, true);
    }
    if (readonly || !win.Sortable) return;
    regionEl.querySelectorAll(".hp-list").forEach((list) => {
      if (list.dataset.hpSortable) return;
      list.dataset.hpSortable = "1";
      win.Sortable.create(list, {
        group: "canvas",
        draggable: ".hp-block",
        filter: ".hp-empty",
        animation: 150,
        forceFallback: true,
        fallbackOnBody: true,
        swapThreshold: 0.5,
        onEnd: (evt) => {
          if (evt.from === evt.to && evt.oldDraggableIndex === evt.newDraggableIndex) return;
          run({ op: "move", id: evt.item.dataset.blockId, parentId: evt.to.dataset.parent || null, index: evt.newDraggableIndex });
        },
      });
    });
  }

  iframe.addEventListener("load", () => { initCanvas(); highlight(); });
  // The canvas may have finished loading before this deferred script ran.
  if (iframe.contentDocument && iframe.contentDocument.readyState === "complete" && iframe.contentWindow.Sortable) initCanvas();
  initLayers();

  // ---------------------------------------------------------------- device preview
  document.querySelectorAll(".devices [data-device]").forEach((btn) =>
    btn.addEventListener("click", () => {
      document.querySelectorAll(".devices button").forEach((b) => b.classList.toggle("on", b === btn));
      const c = document.getElementById("ed-canvas");
      c.classList.remove("tablet", "phone");
      if (btn.dataset.device) c.classList.add(btn.dataset.device);
    }));

  // For debugging in the browser console.
  window.hpEditor = { get selected() { return selected; }, refresh: () => queue(refresh) };
  status("saved", "All changes saved");
})();
