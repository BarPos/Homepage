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

  // ---------------------------------------------------------------- palette: drag onto the page or into Layers, or click
  // One pointer-based drag for both targets (SortableJS cannot cross from this page into the canvas frame). The drop
  // position is computed from where the pointer is released, and the nesting rules are applied while dragging.
  const rules = JSON.parse(document.getElementById("block-rules")?.textContent || "{}");

  function canPlace(type, parentType) {
    const r = rules[type];
    if (!r) return false;
    if (r.parents && !r.parents.includes(parentType || "")) return false;
    if (!parentType) return true;
    const p = rules[parentType];
    return !!(p && p.container && (!p.children || p.children.includes(type)));
  }

  function isHorizontal(list, kids) {
    if (kids.length < 2) return getComputedStyle(list).display.includes("flex") && getComputedStyle(list).flexDirection.startsWith("row");
    const a = kids[0].getBoundingClientRect(), b = kids[1].getBoundingClientRect();
    return b.left >= a.right - 2 && Math.abs(b.top - a.top) < a.height;
  }

  function indexIn(kids, horizontal, x, y) {
    for (let i = 0; i < kids.length; i++) {
      const r = kids[i].getBoundingClientRect();
      if (horizontal ? x < r.left + r.width / 2 : y < r.top + r.height / 2) return i;
    }
    return kids.length;
  }

  /** Where a block of `type` would land in the canvas at frame coordinates (x, y), or null. */
  function canvasTarget(type, x, y) {
    const doc = iframe.contentDocument;
    const regionEl = doc && doc.querySelector('[data-region="' + region + '"]');
    if (!regionEl) return null;
    const root = regionEl.querySelector('.hp-list[data-parent=""]');
    let el = doc.elementFromPoint(x, y);
    let list = el && regionEl.contains(el) ? el.closest(".hp-list") : null;
    if (!list || !regionEl.contains(list)) list = root; // below or beside the content: the end of the page
    // Over a container's padding (outside its inner list): drop into that container.
    const container = el && regionEl.contains(el) ? el.closest(".hp-block[data-container]") : null;
    if (container && list.contains(container)) {
      const inner = container.querySelector('.hp-list[data-parent="' + CSS.escape(container.dataset.blockId) + '"]');
      if (inner) list = inner;
    }
    let anchor = el && list.contains(el) ? el : null;
    // Climb out of containers that do not accept this block (e.g. a section dropped inside a section).
    while (list) {
      const owner = list.dataset.parent ? doc.querySelector('.hp-block[data-block-id="' + CSS.escape(list.dataset.parent) + '"]') : null;
      if (canPlace(type, owner ? owner.dataset.blockType : "")) break;
      if (!owner) return null;
      anchor = owner;
      list = owner.parentElement.closest(".hp-list");
    }
    if (!list) return null;
    const kids = Array.from(list.children).filter((c) => c.classList.contains("hp-block"));
    const horizontal = isHorizontal(list, kids);
    let index = indexIn(kids, horizontal, x, y);
    // When we climbed out of a container, put the block right after that container.
    if (anchor && anchor.classList.contains("hp-block") && anchor.parentElement === list && kids.includes(anchor) && !anchor.contains(el)) index = kids.indexOf(anchor) + 1;
    return { where: "canvas", parentId: list.dataset.parent || null, index, list, kids, horizontal };
  }

  /** Where a block would land in the layers tree at page coordinates, or null. */
  function layersTarget(type, el, y) {
    let ul, index;
    const li = el.closest("li[data-id]");
    if (li && layersEl.contains(li)) {
      const row = li.querySelector(":scope > .row").getBoundingClientRect();
      const inner = li.querySelector(":scope > ul");
      if (inner && canPlace(type, li.dataset.type) && y >= row.top + row.height * 0.3 && y <= row.bottom) {
        ul = inner; index = inner.children.length; // onto a container's row: inside it, at the end
      } else {
        ul = li.parentElement;
        index = Array.from(ul.children).indexOf(li) + (y > row.top + row.height / 2 ? 1 : 0);
      }
    } else {
      ul = el.closest("ul[data-parent]") || layersEl.querySelector("ul.layers");
      index = ul ? ul.children.length : 0;
    }
    while (ul) {
      const ownerLi = ul.dataset.parent ? ul.closest("li[data-id]") : null;
      if (canPlace(type, ownerLi ? ownerLi.dataset.type : "")) break;
      if (!ownerLi) return null;
      index = Array.from(ownerLi.parentElement.children).indexOf(ownerLi) + 1;
      ul = ownerLi.parentElement;
    }
    return ul ? { where: "layers", parentId: ul.dataset.parent || null, index, list: ul } : null;
  }

  function findTarget(type, x, y) {
    const fr = iframe.getBoundingClientRect();
    if (x >= fr.left && x <= fr.right && y >= fr.top && y <= fr.bottom) return canvasTarget(type, x - fr.left, y - fr.top);
    const el = document.elementFromPoint(x, y);
    if (el && layersEl.contains(el)) return layersTarget(type, el, y);
    return null;
  }

  let dropLine = null, dropOutline = null;
  function clearTarget() {
    dropLine?.remove(); dropLine = null;
    dropOutline?.classList.remove("hp-drop-target"); dropOutline = null;
  }
  function showTarget(t) {
    clearTarget();
    if (!t) return;
    const doc = t.where === "canvas" ? iframe.contentDocument : document;
    const kids = t.where === "canvas" ? t.kids : Array.from(t.list.children);
    if (kids.length === 0) { dropOutline = t.list; t.list.classList.add("hp-drop-target"); return; }
    const lr = t.list.getBoundingClientRect();
    const ref = kids[Math.min(t.index, kids.length - 1)].getBoundingClientRect();
    const after = t.index >= kids.length;
    dropLine = doc.createElement("div");
    dropLine.className = "hp-drop-line";
    if (t.horizontal) {
      Object.assign(dropLine.style, { left: (after ? ref.right + 2 : ref.left - 3) + "px", top: ref.top + "px", width: "3px", height: ref.height + "px" });
    } else {
      Object.assign(dropLine.style, { left: lr.left + "px", top: (after ? ref.bottom + 1 : ref.top - 2) + "px", width: lr.width + "px", height: "3px" });
    }
    doc.body.appendChild(dropLine);
  }

  function insertAt(type, parentId, index) {
    run({ op: "insert", type, parentId, index }, (r) => select(r.newId));
  }

  document.querySelectorAll(".palette").forEach((ul) => {
    ul.addEventListener("pointerdown", (e) => {
      const li = e.target.closest("li[data-type]");
      if (!li || readonly || e.button !== 0) return;
      e.preventDefault();
      const type = li.dataset.type;
      const sx = e.clientX, sy = e.clientY;
      let moved = false, ghost = null;
      li.setPointerCapture(e.pointerId);
      const move = (ev) => {
        if (!moved && Math.hypot(ev.clientX - sx, ev.clientY - sy) < 4) return;
        if (!moved) {
          moved = true;
          ghost = li.cloneNode(true);
          ghost.className = "palette-ghost";
          document.body.appendChild(ghost);
        }
        ghost.style.left = ev.clientX + 12 + "px";
        ghost.style.top = ev.clientY + 8 + "px";
        showTarget(findTarget(type, ev.clientX, ev.clientY));
      };
      const end = (ev, cancelled) => {
        li.removeEventListener("pointermove", move);
        li.removeEventListener("pointerup", up);
        li.removeEventListener("pointercancel", cancel);
        try { li.releasePointerCapture(ev.pointerId); } catch { /* already released */ }
        ghost?.remove();
        clearTarget();
        if (cancelled) return;
        if (!moved) { // a click: inside the selected container, or after the selected block
          const p = insertPoint();
          insertAt(type, p.parentId, p.index);
          return;
        }
        const t = findTarget(type, ev.clientX, ev.clientY);
        if (t) insertAt(type, t.parentId, t.index);
        else window.hpFlash && hpFlash("That block cannot be placed there.", "error");
      };
      const up = (ev) => end(ev, false);
      const cancel = (ev) => end(ev, true);
      li.addEventListener("pointermove", move);
      li.addEventListener("pointerup", up);
      li.addEventListener("pointercancel", cancel);
    });
    ul.addEventListener("keydown", (e) => {
      const li = e.target.closest("li[data-type]");
      if (!li || readonly || (e.key !== "Enter" && e.key !== " ")) return;
      e.preventDefault();
      const p = insertPoint();
      insertAt(li.dataset.type, p.parentId, p.index);
    });
  });

  // ---------------------------------------------------------------- layers tree (drag from palette, reorder, nest)
  function initLayers() {
    if (readonly) return;
    layersEl.querySelectorAll("ul[data-parent]").forEach((ul) => {
      Sortable.create(ul, {
        group: { name: "layers", pull: true, put: true }, // reordering only; palette drops use the pointer drag above
        animation: 150,
        forceFallback: true,
        handle: ".row",
        fallbackOnBody: true,
        swapThreshold: 0.6,
        onEnd: (evt) => {
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
