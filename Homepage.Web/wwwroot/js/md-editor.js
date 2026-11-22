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
    const host = document.createElement("div");
    wrap.append(bar, host);
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
      previewStyle: "tab",
      height: "auto",
      minHeight: "280px",
      usageStatistics: false,
      autofocus: false,
      hideModeSwitch: true,
      theme: dark() ? "dark" : "light",
      placeholder: "Write here…",
      toolbarItems: [["heading", "bold", "italic", "strike"], ["hr", "quote"], ["ul", "ol", "task"], ["table", "link", { name: "sitelink", tooltip: "Link to a page, blog or post", el: siteLinkButton() }], ["code", "codeblock"]],
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

    // "Link to a page": the site's link chooser; the link stores a reference (page:12, …) that follows renames.
    function openSiteLink() {
      const dlg = document.getElementById("link-dialog");
      if (!dlg) return;
      window.hpLinkPick = (value, title) => {
        const [from, to] = editor.getSelection();
        const selected = editor.getSelectedText ? editor.getSelectedText(from, to) : "";
        const url = mode === "wysiwyg" ? REF_HOST + value.replace(":", "/") : value;
        editor.exec("addLink", { linkUrl: url, linkText: selected || title || value });
        editor.focus();
      };
      const opener = document.createElement("button");
      opener.dataset.linkFor = "@markdown";
      opener.hidden = true;
      document.body.appendChild(opener);
      opener.click(); // sets the chooser's target
      opener.remove();
      dlg.showModal();
      dlg.querySelector("input[type=search]")?.focus();
    }
    host.addEventListener("click", (e) => { if (e.target.closest && e.target.closest(".hp-sitelink")) { e.preventDefault(); openSiteLink(); } });

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

  function siteLinkButton() {
    const b = document.createElement("button");
    b.type = "button";
    b.className = "toastui-editor-toolbar-icons hp-sitelink";
    b.setAttribute("aria-label", "Link to a page, blog or post");
    b.innerHTML = '<svg viewBox="0 0 16 16" width="16" height="16" aria-hidden="true"><path d="M3 2h6l4 4v8H3z M9 2v4h4" fill="none" stroke="currentColor" stroke-width="1.4" stroke-linejoin="round"/><path d="M6 10.5h4M8 8.5v4" stroke="currentColor" stroke-width="1.4" stroke-linecap="round"/></svg>';
    b.style.backgroundImage = "none";
    return b;
  }

  // Exposed for checks: the protect/restore round trip without an editor.
  window.hpMarkdownGuard = { protect, restore };

  function scan(root) { (root.querySelectorAll ? root : document).querySelectorAll("textarea[data-md-editor]").forEach(init); }
  document.addEventListener("DOMContentLoaded", () => scan(document));
  document.addEventListener("htmx:load", (e) => scan(e.target));
})();
