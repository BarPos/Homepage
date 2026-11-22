// The field list of the form editor (admin/forms/{id}): Alpine data, kept as JSON in a hidden input.
document.addEventListener('alpine:init', () => {
  window.Alpine.data('hpFormBuilder', function () {
    const initial = JSON.parse(this.$el.dataset.fields || '[]');
    const names = JSON.parse(this.$el.dataset.typeNames || '{}');
    let n = 0;
    return {
      fields: initial.map(f => ({ ...f, id: ++n })),
      open: null,
      consent: this.$el.dataset.consent || '',
      typeName(t) { return names[t] || t; },
      add(type, label, options) {
        const f = { id: ++n, key: '', label, type, required: type === 'Email' || type === 'Textarea', placeholder: '', help: '', options: options || [], width: 'Full' };
        this.fields.push(f);
        this.open = f.id;
        this.$nextTick(() => document.getElementById('fl' + f.id)?.focus());
      },
      move(i, d) {
        const j = i + d;
        if (j < 0 || j >= this.fields.length) return;
        this.fields.splice(j, 0, this.fields.splice(i, 1)[0]);
      },
      json() {
        return JSON.stringify(this.fields.map(({ id, ...f }) => ({ ...f, options: f.options.map(o => o.trim()).filter(Boolean) })));
      },
    };
  });
});
