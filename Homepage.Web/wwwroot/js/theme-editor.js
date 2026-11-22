// Live theme preview: form values become CSS custom properties on the preview iframe's root element.
// Nothing reaches the server until "Save theme".
(function () {
  const form = document.getElementById("theme-form");
  const frame = document.getElementById("theme-preview");
  if (!form || !frame) return;

  const val = (name) => form.elements[name]?.value;
  const lum = (hex) => {
    const c = [1, 3, 5].map((i) => parseInt(hex.substr(i, 2), 16) / 255)
      .map((v) => (v <= 0.03928 ? v / 12.92 : Math.pow((v + 0.055) / 1.055, 2.4)));
    return 0.2126 * c[0] + 0.7152 * c[1] + 0.0722 * c[2];
  };
  const contrast = (a, b) => { const [x, y] = [lum(a), lum(b)]; return (Math.max(x, y) + 0.05) / (Math.min(x, y) + 0.05); };
  const pairs = { text: "bg", muted: "bg", "primary-text": "primary", "accent-text": "accent" };

  function apply() {
    const root = frame.contentDocument?.documentElement;
    const mode = val("colorMode");
    const scheme = mode === "dark" ? "dark" : "light";
    form.querySelectorAll('input[type=color]').forEach((input) => {
      const [s, key] = input.name.split(".");
      if (root && s === scheme) root.style.setProperty("--color-" + key, input.value);
    });
    if (root) {
      const stack = (name) => form.elements[name].selectedOptions[0]?.dataset.stack || "";
      root.style.setProperty("--font-body", stack("bodyFont"));
      root.style.setProperty("--font-heading", stack("headingFont"));
      root.style.setProperty("--font-size", val("baseSize") + "px");
      root.style.setProperty("--scale", val("scale"));
      root.style.setProperty("--line-height", val("lineHeight"));
      root.style.setProperty("--radius", val("radius") + "px");
      root.style.setProperty("--space", val("spacingUnit") + "rem");
      root.style.setProperty("--container", val("containerWidth") + "px");
      root.style.setProperty("--shadow", {
        none: "none",
        soft: "0 1px 2px rgb(16 18 27 / .05), 0 10px 30px -12px rgb(16 18 27 / .18)",
        strong: "0 2px 6px rgb(0 0 0 / .08), 0 28px 60px -20px rgb(0 0 0 / .38)",
      }[val("shadow")]);
      root.style.setProperty("--color-scheme", scheme);
      const radius = parseFloat(val("radius"));
      root.style.setProperty("--btn-radius", radius >= 10 ? "999px" : radius + "px");
      const glass = form.elements["glass"].checked;
      root.dataset.glass = glass ? "on" : "off";
      root.dataset.backdrop = val("backdrop");
      root.style.setProperty("--glass-blur", (glass ? val("glassBlur") : 0) + "px");
      root.style.setProperty("--glass-alpha", glass ? val("glassOpacity") : "1");
      const darkBg = lum(val(scheme + ".bg")) < 0.18;
      root.style.setProperty("--glass-edge", darkBg ? "rgb(255 255 255 / .10)" : "rgb(255 255 255 / .70)");
      root.style.setProperty("--glass-highlight", darkBg ? "rgb(255 255 255 / .07)" : "rgb(255 255 255 / .85)");
      root.style.setProperty("--glass-sheen", darkBg ? ".06" : ".45");
    }
    form.querySelectorAll("[data-out]").forEach((o) => {
      const k = o.dataset.out;
      o.textContent = val(k) + (["baseSize", "radius", "containerWidth", "glassBlur"].includes(k) ? " px" : "");
    });
    for (const s of ["light", "dark"]) {
      for (const [fg, bg] of Object.entries(pairs)) {
        const el = form.querySelector('[data-contrast="' + s + "." + fg + '"]');
        if (!el) continue;
        const r = contrast(val(s + "." + fg), val(s + "." + bg));
        el.textContent = r.toFixed(1) + ":1";
        el.className = "contrast small " + (r >= 4.5 ? "good" : "bad");
        el.title = r >= 4.5 ? "Meets WCAG AA for body text" : "Below WCAG AA (4.5:1) against " + bg;
      }
    }
  }

  form.addEventListener("input", apply);
  form.addEventListener("change", apply);
  frame.addEventListener("load", apply);
  apply();
})();
