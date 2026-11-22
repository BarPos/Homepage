// Markdown blocks in the inspector: a Toast UI editor with two modes, rich text and Markdown (tabs above it). The
// block's textarea stays in the form, hidden; the editor writes Markdown into it and fires "input", which saves the
// block. Rich text cannot represent everything Markdown can (footnotes, HTML, {new} link attributes), so those parts
// are kept aside while in rich text: each shows as a small chip and is put back exactly, character for character.
(function () {
  "use strict";
  const MODE_KEY = "hp_md_mode";
  const TOKEN = /⟦hp:(\d+)⟧/g;
  const WIDGET = /\$\$widget0 ⟦hp:(\d+)⟧\$\$/g;
  const dark = () => window.matchMedia && window.matchMedia("(prefers-color-scheme: dark)").matches;
  const savedMode = () => { try { return localStorage.getItem(MODE_KEY) === "markdown" ? "markdown" : "wysiwyg"; } catch (e) { return "wysiwyg"; } };

  // ---- protecting what rich text would change
  function protect(md, chunks) {
    const keep = (text, label) => { chunks.push({ text, label }); return "$$widget0 ⟦hp:" + (chunks.length - 1) + "⟧$$"; };
    const lines = md.split("\n");
    const out = [];
    let fence = null;
    for (let i = 0; i < lines.length; i++) {
      const line = lines[i];
      const f = line.match(/^\s{0,3}(`{3,}|~{3,})/);
      if (fence) { out.push(line); if (f && f[1][0] === fence[0] && f[1].length >= fence.length) fence = null; continue; }
      if (f) { fence = f[1]; out.push(line); continue; }
      // a footnote definition (with its indented continuation lines) or an HTML block: kept whole, up to a blank line
      const footnote = /^\[\^[^\]]+\]:/.test(line);
      const html = /^\s{0,3}<(\/?[a-zA-Z][\w-]*[\s>/]|!--)/.test(line);
      if (footnote || html) {
        const block = [line];
        while (i + 1 < lines.length && lines[i + 1].trim() !== "" && (html || /^\s/.test(lines[i + 1]) || !/^\[\^/.test(lines[i + 1]))) block.push(lines[++i]);
        if (footnote) while (i + 2 < lines.length && lines[i + 1].trim() === "" && /^ {2,}\S/.test(lines[i + 2])) { block.push(lines[++i], lines[++i]); }
        const first = footnote ? line.match(/^\[\^[^\]]+\]/)[0] + " footnote" : line.trim();
        out.push(keep(block.join("\n"), first.length > 32 ? first.slice(0, 30) + "…" : first));
        continue;
      }
      out.push(protectInline(line, keep));
    }
    return out.join("\n");
  }

  // Inline HTML tags and {…} attributes after links, outside `code`.
  function protectInline(line, keep) {
    return line.split(/(`+[^`]*`+)/).map((part, n) => n % 2 ? part : part
      .replace(/<!--[\s\S]*?-->|<\/?[a-zA-Z][\w-]*(?:\s[^<>]*)?\/?>/g, (m) => keep(m, m.length > 24 ? m.slice(0, 22) + "…" : m))
      .replace(/(?<=[)\]])\{[^{}\n]{1,120}\}/g, (m) => keep(m, m))).join("");
  }

  // Rich text only keeps links with known schemes, so references (page:12) travel as https://hp-ref.invalid/page/12.
  const REF_HOST = "https://hp-ref.invalid/";
  const toRichRefs = (md) => md.replace(/\]\((page|post|blog):(\d+)(?=[\s)])/g, (_, k, n) => "](" + REF_HOST + k + "/" + n);
  const fromRichRefs = (md) => md.split(REF_HOST).map((part, i) => i === 0 ? part : part.replace(/^(page|post|blog)\/(\d+)/, "$1:$2")).join("");
  const restore = (md, chunks) => fromRichRefs(md.replace(WIDGET, (_, n) => chunks[+n] ? chunks[+n].text : "").replace(TOKEN, (_, n) => chunks[+n] ? chunks[+n].text : ""));

  function init(textarea) {
    if (textarea._hpEditor || !window.toastui) return;
    const readonly = textarea.matches(":disabled");
    const wrap = document.createElement("div");
    wrap.className = "md-wrap";
    const bar = document.createElement("div");
    bar.className = "md-bar";
    bar.innerHTML =
      '<div class="an-tabs md-tabs" role="tablist" aria-label="Editor mode">' +
      '<button type="button" role="tab" data-mode="wysiwyg">Rich text</button><button type="button" role="tab" data-mode="markdown">Markdown</button></div>' +
      '<button type="button" class="btn-sm md-expand" title="Bigger editor" aria-label="Bigger editor" aria-pressed="false">⤢</button>';
    const tools = document.createElement("div");
    tools.className = "md-toolbar";
    tools.setAttribute("role", "toolbar");
    tools.setAttribute("aria-label", "Formatting");
    tools.innerHTML = TOOLBAR;
    const host = document.createElement("div");
    wrap.append(bar, tools, host);
    // The toolbar's own controls must not save the block (only the text and the alignment do).
    ["input", "change"].forEach((t) => tools.addEventListener(t, (e) => e.stopPropagation()));
    if (window.hpEnhanceSelects) window.hpEnhanceSelects(tools);
    textarea.parentNode.insertBefore(wrap, textarea);
    textarea.hidden = true;
    if (readonly) wrap.classList.add("md-readonly");

    let chunks = [];
    let mode = "markdown";
    let switching = false;
    const editor = new window.toastui.Editor({
      el: host,
      initialValue: textarea.value,
      initialEditType: "markdown",
      previewStyle: "vertical", // its preview pane is hidden: the canvas next to it is the preview
      height: "auto",
      minHeight: "280px",
      usageStatistics: false,
      autofocus: false,
      hideModeSwitch: true,
      theme: dark() ? "dark" : "light",
      placeholder: "Write here…",
      toolbarItems: [], // replaced by the toolbar above (admin styles, wraps instead of hiding buttons)
      widgetRules: [{
        rule: /⟦hp:\d+⟧/,
        toDOM(text) {
          const n = +(/\d+/.exec(text) || [0])[0];
          const chip = document.createElement("span");
          chip.className = "md-kept";
          chip.textContent = chunks[n] ? chunks[n].label : "kept";
          chip.title = (chunks[n] ? chunks[n].text : "") + "\n\nKept as written. Switch to Markdown to change it.";
          return chip;
        },
      }],
    });
    textarea._hpEditor = editor;

    // Links: the site's link chooser (pages, blogs, posts: stored as page:12, … so they follow renames) or any address.
    // A selection made just before (a double-click, then Ctrl+K) reaches the editor a moment later: wait for it.
    function openLinkDialog() { setTimeout(openLinkDialogNow, 40); }
    function openLinkDialogNow() {
      const dlg = document.getElementById("link-dialog");
      if (!dlg) return;
      const [from, to] = editor.getSelection();
      const dom = window.getSelection();
      const domText = dom && dom.anchorNode && host.contains(dom.anchorNode) ? dom.toString() : "";
      const selected = ((editor.getSelectedText ? editor.getSelectedText(from, to) : "") || domText || "").trim();
      window.hpLinkPick = (value, title, newTab) => {
        editor.focus();
        if (mode === "wysiwyg") editor.setSelection(from, to);
        const ref = /^(page|post|blog):\d+$/.test(value);
        const url = ref && mode === "wysiwyg" ? REF_HOST + value.replace(":", "/") : value;
        editor.exec("addLink", { linkUrl: url, linkText: selected || title || value });
        if (newTab) editor.insertText("{new}");
      };
      const opener = document.createElement("button");
      opener.dataset.linkFor = "@markdown";
      opener.hidden = true;
      document.body.appendChild(opener);
      opener.click(); // tells the chooser where the choice goes
      opener.remove();
      dlg.querySelectorAll("[data-link-external]").forEach((el) => { el.hidden = false; });
      const text = dlg.querySelector("[data-link-external-text]");
      if (text) text.value = selected;
      const url = dlg.querySelector("[data-link-external-url]");
      if (url) url.value = "";
      const nt = dlg.querySelector("[data-link-external-newtab]");
      if (nt) nt.checked = false;
      dlg.showModal();
      dlg.querySelector("input[type=search]")?.focus();
    }

    const alignSelect = textarea.form && textarea.form.querySelector('[name="p.Align"]');
    const showAlign = () => { wrap.dataset.align = alignSelect ? alignSelect.value.toLowerCase() : "left"; refreshTools(); };
    if (alignSelect) alignSelect.addEventListener("change", showAlign);

    const run = (cmd, payload) => { editor.focus(); editor.exec(cmd, payload); refreshTools(); };
    tools.addEventListener("click", (e) => {
      const b = e.target.closest("[data-cmd]");
      if (!b || readonly) return;
      e.preventDefault();
      const cmd = b.dataset.cmd;
      if (cmd === "link") return openLinkDialog();
      if (cmd === "table") return run("addTable", { rowCount: 3, columnCount: 2 });
      if (cmd === "align") {
        if (!alignSelect) return;
        alignSelect.value = b.dataset.value;
        alignSelect.dispatchEvent(new Event("change", { bubbles: true })); // saves the block, like the field below
        return;
      }
      run(cmd);
    });
    tools.querySelector("[data-block]")?.addEventListener("change", (e) => {
      const v = e.target.value;
      if (v === "p") run(mode === "wysiwyg" ? "heading" : "heading", { level: 0 });
      else run("heading", { level: +v });
    });
    tools.addEventListener("mousedown", (e) => { if (e.target.closest("[data-cmd]")) e.preventDefault(); }); // keep the selection

    // The heading level at the cursor: from the element in rich text, from the # marks in Markdown.
    function headingLevel() {
      if (mode === "wysiwyg") {
        const node = window.getSelection() && window.getSelection().anchorNode;
        const el = node && (node.nodeType === 1 ? node : node.parentElement);
        const h = el && el.closest && el.closest("h1,h2,h3,h4,h5,h6");
        return h && host.contains(h) ? +h.tagName[1] : 0;
      }
      const [start] = editor.getSelection();
      const line = (editor.getMarkdown().split("\n")[(start && start[0] ? start[0] : 1) - 1]) || "";
      const m = line.match(/^(#{1,6})\s/);
      return m ? m[1].length : 0;
    }

    // Which formatting is at the cursor (bold, list, …), shown on the buttons.
    let state = {};
    editor.eventEmitter.listen("changeToolbarState", (s) => { state = (s && (s.toolbarState || s)) || {}; refreshTools(); });
    function refreshTools() {
      const on = (k) => !!(state[k] && (state[k].active || state[k] === true));
      tools.querySelectorAll("[data-cmd]").forEach((b) => {
        const k = b.dataset.state;
        const active = b.dataset.cmd === "align" ? (alignSelect && alignSelect.value === b.dataset.value) : k ? on(k) : false;
        b.classList.toggle("on", !!active);
        b.setAttribute("aria-pressed", active ? "true" : "false");
      });
      const sel = tools.querySelector("[data-block]");
      if (sel) {
        const h = state.heading && state.heading.active ? String(headingLevel() || "") : "";
        const v = /^[1-4]$/.test(h) ? h : "p";
        if (sel.value !== v) { sel.value = v; window.hpRefreshSelects && window.hpRefreshSelects(); }
      }
    }
    showAlign();
    host.addEventListener("keydown", (e) => {
      if ((e.ctrlKey || e.metaKey) && !e.shiftKey && e.key.toLowerCase() === "k") { e.preventDefault(); e.stopPropagation(); openLinkDialog(); }
    }, true);

    const markdown = () => (mode === "wysiwyg" ? restore(editor.getMarkdown(), chunks) : editor.getMarkdown());
    textarea._hpMarkdown = markdown;

    function setMode(next, remember) {
      if (next === mode) return;
      switching = true;
      const md = markdown();
      if (next === "wysiwyg") {
        chunks = [];
        editor.setMarkdown(toRichRefs(protect(md, chunks)), false); // still in Markdown: the protected text converts safely
        editor.changeMode("wysiwyg", true);
      } else {
        editor.changeMode("markdown", true);
        editor.setMarkdown(md, false);
      }
      mode = next;
      switching = false;
      bar.querySelectorAll("[data-mode]").forEach((b) => {
        const on = b.dataset.mode === mode;
        b.classList.toggle("on", on);
        b.setAttribute("aria-selected", on ? "true" : "false");
      });
      if (remember) { try { localStorage.setItem(MODE_KEY, mode); } catch (e) { /* ignore */ } }
    }
    bar.querySelectorAll("[data-mode]").forEach((b) => b.addEventListener("click", () => setMode(b.dataset.mode, true)));
    setMode(savedMode(), false);
    if (mode === "markdown") bar.querySelector('[data-mode="markdown"]').classList.add("on");

    // The editor's own inputs must not reach the inspector form (they would save stale text); only the textarea does.
    ["input", "change", "keyup"].forEach((t) => host.addEventListener(t, (e) => e.stopPropagation()));
    let timer = 0;
    editor.on("change", () => {
      if (switching || readonly) return;
      clearTimeout(timer);
      timer = setTimeout(() => {
        const md = markdown();
        if (md === textarea.value) return;
        textarea.value = md;
        textarea.dispatchEvent(new Event("input", { bubbles: true }));
      }, 250);
    });

    const expand = bar.querySelector(".md-expand");
    expand.addEventListener("click", () => {
      const on = wrap.classList.toggle("md-full");
      expand.setAttribute("aria-pressed", on ? "true" : "false");
      expand.textContent = on ? "✕" : "⤢";
      expand.title = on ? "Back to the panel" : "Bigger editor";
      editor.setHeight(on ? "100%" : "auto");
      requestAnimationFrame(() => window.dispatchEvent(new Event("resize"))); // the toolbar re-measures its width
    });
    document.addEventListener("keydown", (e) => {
      if (e.key === "Escape" && wrap.classList.contains("md-full")) expand.click();
    });
  }

  const ico = (d) => '<svg viewBox="0 0 20 20" aria-hidden="true"><path d="' + d + '"/></svg>';
  const btn = (cmd, label, inner, extra = "") => '<button type="button" class="md-tool" data-cmd="' + cmd + '" title="' + label + '" aria-label="' + label + '"' + extra + ">" + inner + "</button>";
  const TOOLBAR =
    '<select data-block aria-label="Paragraph or heading" class="md-block-select"><option value="p">Paragraph</option><option value="1">Heading 1</option><option value="2">Heading 2</option><option value="3">Heading 3</option><option value="4">Heading 4</option></select>' +
    '<span class="md-sep"></span>' +
    btn("bold", "Bold (Ctrl+B)", "<b>B</b>", ' data-state="strong"') +
    btn("italic", "Italic (Ctrl+I)", "<i>I</i>", ' data-state="emph"') +
    btn("strike", "Strikethrough", "<s>S</s>", ' data-state="strike"') +
    btn("code", "Code", ico("M7 6 3 10l4 4M13 6l4 4-4 4"), ' data-state="code"') +
    '<span class="md-sep"></span>' +
    btn("link", "Link: a page, blog, post or address (Ctrl+K)", ico("M8.5 11.5a3 3 0 0 0 4.2 0l2.6-2.6a3 3 0 0 0-4.2-4.2l-.9.9M11.5 8.5a3 3 0 0 0-4.2 0l-2.6 2.6a3 3 0 0 0 4.2 4.2l.9-.9")) +
    '<span class="md-sep"></span>' +
    btn("bulletList", "Bulleted list", ico("M8 5h9M8 10h9M8 15h9M3.5 5h.01M3.5 10h.01M3.5 15h.01"), ' data-state="bulletList"') +
    btn("orderedList", "Numbered list", ico("M8 5h9M8 10h9M8 15h9M3 4h1v3M3 12.5c0-.8 2-.8 2 0s-2 1.2-2 2.5h2"), ' data-state="orderedList"') +
    btn("taskList", "Checklist", ico("M3 5l1.5 1.5L7 4M3 13l1.5 1.5L7 12M10 5h7M10 13h7"), ' data-state="taskList"') +
    btn("blockQuote", "Quote", ico("M4 5v10M8 7h8M8 11h8M8 15h5"), ' data-state="blockQuote"') +
    '<span class="md-sep"></span>' +
    btn("codeBlock", "Code block", ico("M3 4h14v12H3zM7 8l-2 2 2 2M13 8l2 2-2 2"), ' data-state="codeBlock"') +
    btn("table", "Table", ico("M3 4h14v12H3zM3 8h14M3 12h14M9 4v12")) +
    btn("hr", "Divider", ico("M3 10h14")) +
    '<span class="md-sep"></span>' +
    btn("align", "Align left", ico("M3 5h14M3 9h9M3 13h14M3 17h9"), ' data-value="Left"') +
    btn("align", "Center", ico("M3 5h14M6 9h8M3 13h14M6 17h8"), ' data-value="Center"') +
    btn("align", "Align right", ico("M3 5h14M8 9h9M3 13h14M8 17h9"), ' data-value="Right"') +
    btn("align", "Justify", ico("M3 5h14M3 9h14M3 13h14M3 17h14"), ' data-value="Justify"') +
    '<span class="md-sep"></span>' +
    btn("undo", "Undo (Ctrl+Z)", ico("M7 7H4V4M4.5 7A6 6 0 1 1 4 12")) +
    btn("redo", "Redo (Ctrl+Shift+Z)", ico("M13 7h3V4M15.5 7A6 6 0 1 0 16 12"));

  // Exposed for checks: the protect/restore round trip without an editor.
  window.hpMarkdownGuard = { protect, restore };

  function scan(root) { (root.querySelectorAll ? root : document).querySelectorAll("textarea[data-md-editor]").forEach(init); }
  document.addEventListener("DOMContentLoaded", () => scan(document));
  document.addEventListener("htmx:load", (e) => scan(e.target));
})();
