// Custom dropdowns for every <select> in the admin. The native select stays in the form (hidden) and remains the
// source of truth: choosing an option sets its value and fires "input" and "change", so forms, htmx triggers,
// Alpine x-model and data-autosubmit work as before. Opt out with data-native. Keyboard: arrows, Home/End,
// Enter/Space, Escape, Tab, and typing to jump. A search box comes with long lists (more than 10 options) and with
// selects marked data-search (pickers of pages, media, …). Options with data-thumb show that image.
(function () {
  "use strict";
  let open = null; // { select, button, panel, items, active }

  function label(select) {
    if (select.getAttribute("aria-label")) return select.getAttribute("aria-label");
    const l = select.id && document.querySelector('label[for="' + CSS.escape(select.id) + '"]');
    return l ? l.textContent.trim() : "";
  }

  function render(select) {
    const btn = select._hpButton;
    if (!btn) return;
    const opt = select.options[select.selectedIndex];
    const value = btn.querySelector(".hp-select-value");
    value.textContent = opt ? opt.textContent : "";
    if (opt && opt.dataset.thumb) {
      const img = document.createElement("img");
      img.className = "hp-select-thumb";
      img.src = opt.dataset.thumb;
      img.alt = "";
      value.prepend(img);
    }
    btn.disabled = select.matches(":disabled");
  }

  function enhance(select) {
    if (select._hpButton || select.multiple || select.size > 1 || "native" in select.dataset) return;
    const wrap = document.createElement("div");
    wrap.className = "hp-select";
    if (select.getAttribute("style")) wrap.setAttribute("style", select.getAttribute("style"));
    select.parentNode.insertBefore(wrap, select);
    wrap.appendChild(select);
    select.classList.add("hp-select-native");
    select.tabIndex = -1;
    select.setAttribute("aria-hidden", "true");

    const btn = document.createElement("button");
    btn.type = "button";
    btn.className = "hp-select-button";
    btn.setAttribute("aria-haspopup", "listbox");
    btn.setAttribute("aria-expanded", "false");
    const name = label(select);
    if (name) btn.setAttribute("aria-label", name);
    if (select.id) {
      btn.id = select.id + "-dd";
      const l = document.querySelector('label[for="' + CSS.escape(select.id) + '"]');
      if (l) l.htmlFor = btn.id; // clicking the label opens the dropdown
    }
    btn.innerHTML = '<span class="hp-select-value"></span><svg class="hp-select-chevron" viewBox="0 0 16 16" aria-hidden="true"><path d="M4 6l4 4 4-4" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"/></svg>';
    wrap.appendChild(btn);
    select._hpButton = btn;

    btn.addEventListener("click", () => (open && open.select === select ? close(true) : show(select)));
    btn.addEventListener("keydown", (e) => {
      if (["ArrowDown", "ArrowUp", "Enter", " "].includes(e.key)) { e.preventDefault(); show(select); }
      else if (e.key.length === 1 && /\S/.test(e.key)) { jump(select, e.key); }
    });
    select.addEventListener("change", () => render(select));
    new MutationObserver(() => render(select)).observe(select, { childList: true, subtree: true, attributes: true, attributeFilter: ["disabled"] });
    const fieldset = select.closest("fieldset");
    if (fieldset) new MutationObserver(() => render(select)).observe(fieldset, { attributes: true, attributeFilter: ["disabled"] });
    render(select);
  }

  // Typing on the closed button picks the next option starting with that letter (like a native select).
  function jump(select, ch) {
    const opts = [...select.options].filter((o) => !o.disabled && !o.hidden);
    const start = opts.indexOf(select.options[select.selectedIndex]);
    const next = opts.slice(start + 1).concat(opts.slice(0, start + 1)).find((o) => o.textContent.trim().toLowerCase().startsWith(ch.toLowerCase()));
    if (next) choose(select, next.value, false);
  }

  function choose(select, value, closeAfter = true) {
    if (select.value !== value) {
      select.value = value;
      select.dispatchEvent(new Event("input", { bubbles: true }));
      select.dispatchEvent(new Event("change", { bubbles: true }));
    }
    render(select);
    if (closeAfter) close(true);
  }

  function show(select) {
    close(false);
    const btn = select._hpButton;
    if (btn.disabled) return;
    const host = select.closest("dialog[open]") || document.body; // inside a modal dialog, stay in its top layer
    const panel = document.createElement("div");
    panel.className = "hp-select-panel";
    const many = select.options.length > 10 || ("search" in select.dataset && select.options.length > 1);
    let search = null;
    if (many) {
      search = document.createElement("input");
      search.type = "search";
      search.className = "hp-select-search";
      search.placeholder = "Search…";
      search.setAttribute("aria-label", "Search options");
      panel.appendChild(search);
    }
    const list = document.createElement("ul");
    list.className = "hp-select-list";
    list.setAttribute("role", "listbox");
    if (btn.getAttribute("aria-label")) list.setAttribute("aria-label", btn.getAttribute("aria-label"));
    const items = [];
    let group = null;
    [...select.querySelectorAll("option, optgroup")].forEach((node) => {
      if (node.tagName === "OPTGROUP") {
        group = document.createElement("li");
        group.className = "hp-select-group";
        group.setAttribute("role", "presentation");
        group.textContent = node.label;
        list.appendChild(group);
        return;
      }
      if (node.hidden || node.style.display === "none") return;
      const li = document.createElement("li");
      li.setAttribute("role", "option");
      li.className = "hp-select-option";
      li.textContent = node.textContent;
      li.dataset.value = node.value;
      if (node.dataset.thumb) {
        const img = document.createElement("img");
        img.className = "hp-select-thumb";
        img.src = node.dataset.thumb;
        img.alt = "";
        img.loading = "lazy";
        li.prepend(img);
        li.classList.add("has-thumb");
      }
      if (node.disabled) li.setAttribute("aria-disabled", "true");
      if (node.selected) li.setAttribute("aria-selected", "true");
      li.addEventListener("mousedown", (e) => e.preventDefault()); // keep focus where it is
      li.addEventListener("click", () => { if (!node.disabled) choose(select, node.value); });
      li.addEventListener("mousemove", () => setActive(items.indexOf(li)));
      list.appendChild(li);
      items.push(li);
    });
    panel.appendChild(list);
    host.appendChild(panel);
    open = { select, btn, panel, items, active: -1, search };
    btn.setAttribute("aria-expanded", "true");
    btn.classList.add("open");
    place();
    // A dialog that is still animating in moves (and stops being transformed) when it finishes: place again then.
    if (host.tagName === "DIALOG" && host.getAnimations) host.getAnimations().forEach((an) => an.finished.then(place, () => {}));
    setActive(Math.max(0, items.findIndex((li) => li.getAttribute("aria-selected") === "true")));
    if (search) {
      search.addEventListener("input", () => {
        const q = search.value.trim().toLowerCase();
        items.forEach((li) => { li.hidden = q !== "" && !li.textContent.toLowerCase().includes(q); });
        list.querySelectorAll(".hp-select-group").forEach((g) => { g.hidden = q !== ""; });
        setActive(items.findIndex((li) => !li.hidden));
      });
      search.addEventListener("keydown", keys);
      search.focus();
    } else {
      list.tabIndex = -1;
      list.addEventListener("keydown", keys);
      list.focus({ preventScroll: true });
    }
  }

  function visibleItems() { return open.items.filter((li) => !li.hidden && li.getAttribute("aria-disabled") !== "true"); }

  function setActive(index) {
    if (!open) return;
    open.items.forEach((li, i) => li.classList.toggle("active", i === index));
    open.active = index;
    const li = open.items[index];
    if (li) li.scrollIntoView({ block: "nearest" });
  }

  function keys(e) {
    if (!open) return;
    const vis = visibleItems();
    const cur = vis.indexOf(open.items[open.active]);
    const move = (i) => { const li = vis[Math.max(0, Math.min(vis.length - 1, i))]; if (li) setActive(open.items.indexOf(li)); };
    switch (e.key) {
      case "ArrowDown": e.preventDefault(); move(cur + 1); break;
      case "ArrowUp": e.preventDefault(); move(cur - 1); break;
      case "Home": if (!open.search) { e.preventDefault(); move(0); } break;
      case "End": if (!open.search) { e.preventDefault(); move(vis.length - 1); } break;
      case "PageDown": e.preventDefault(); move(cur + 8); break;
      case "PageUp": e.preventDefault(); move(cur - 8); break;
      case "Enter": e.preventDefault(); if (open.items[open.active]) choose(open.select, open.items[open.active].dataset.value); break;
      case " ": if (!open.search) { e.preventDefault(); if (open.items[open.active]) choose(open.select, open.items[open.active].dataset.value); } break;
      case "Escape": e.preventDefault(); e.stopPropagation(); close(true); break;
      case "Tab": close(false); break;
      default:
        if (!open.search && e.key.length === 1 && /\S/.test(e.key)) {
          const ch = e.key.toLowerCase();
          const next = vis.slice(cur + 1).concat(vis.slice(0, cur + 1)).find((li) => li.textContent.trim().toLowerCase().startsWith(ch));
          if (next) setActive(open.items.indexOf(next));
        }
    }
  }

  // Fixed position under the button (or above it when there is no room), so cards with overflow do not clip it.
  function place() {
    if (!open) return;
    const r = open.btn.getBoundingClientRect();
    const p = open.panel;
    // "fixed" is relative to the viewport, unless an ancestor (a dialog mid-animation) has a transform: measure it.
    p.style.left = "0px";
    p.style.top = "0px";
    const origin = p.getBoundingClientRect();
    const hostRect = { left: origin.left, top: origin.top };
    p.style.minWidth = r.width + "px";
    p.style.maxWidth = Math.max(r.width, Math.min(480, window.innerWidth - 16)) + "px";
    const below = window.innerHeight - r.bottom - 8, above = r.top - 8;
    const height = Math.min(p.scrollHeight, 320);
    const up = below < height && above > below;
    p.classList.toggle("up", up);
    p.style.maxHeight = Math.max(120, Math.min(320, up ? above : below)) + "px";
    const left = Math.min(r.left, window.innerWidth - p.offsetWidth - 8);
    p.style.left = Math.max(8, left) - hostRect.left + "px";
    p.style.top = (up ? r.top - Math.min(p.scrollHeight, parseFloat(p.style.maxHeight)) - 4 : r.bottom + 4) - hostRect.top + "px";
  }

  function close(focusButton) {
    if (!open) return;
    const { btn, panel } = open;
    panel.remove();
    btn.setAttribute("aria-expanded", "false");
    btn.classList.remove("open");
    open = null;
    if (focusButton) btn.focus();
  }

  document.addEventListener("mousedown", (e) => { if (open && !open.panel.contains(e.target) && !open.btn.contains(e.target)) close(false); });
  window.addEventListener("resize", () => close(false));
  document.addEventListener("scroll", (e) => { if (open && !open.panel.contains(e.target)) place(); }, true);

  function enhanceAll(root) {
    (root.querySelectorAll ? root : document).querySelectorAll("select").forEach(enhance);
    if (root.tagName === "SELECT") enhance(root);
  }
  const refreshAll = () => document.querySelectorAll("select").forEach(render);
  document.addEventListener("DOMContentLoaded", () => enhanceAll(document));
  if (document.readyState !== "loading") enhanceAll(document);
  document.addEventListener("htmx:load", (e) => enhanceAll(e.target));      // inspector, lists, … swapped in by htmx
  document.addEventListener("alpine:initialized", refreshAll);             // Alpine may set values after we render
  document.addEventListener("reset", () => setTimeout(refreshAll), true);
  window.hpRefreshSelects = refreshAll;
  window.hpEnhanceSelects = (root) => enhanceAll(root || document); // for controls added later by other scripts
})();
