// One pop-up. The program opens this page in a small corner window that never takes the focus, then posts
// { type: 'toast', toast: { kind, title, text, actions: [{ label, cmd, args, primary }], timeout } }.
// Buttons go back as { cmd, args } (the program runs them and closes the pop-up); { cmd: 'toast.close' } when it is done.
'use strict';
(() => {
  const web = window.chrome?.webview;
  const esc = (v) => String(v ?? '').replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[c]);
  const KIND = { good: 'checkCircle', warn: 'alert', bad: 'xCircle', request: 'requests', info: 'record' };
  const el = document.getElementById('toast');
  let timer = null, left = 0, last = 0;
  const post = (cmd, args = {}) => web?.postMessage({ cmd, args });

  function show(t) {
    const acts = t.actions ?? [];
    el.className = 'toast ' + (t.kind ?? 'info');
    el.innerHTML = `<div class="t-ic">${icon(KIND[t.kind] ?? 'info')}</div><div class="t-body"><div class="t-from">FFACiv Recorder</div><h1>${esc(t.title)}</h1>${t.text ? `<p>${esc(t.text)}</p>` : ''}${acts.length ? `<div class="row">${acts.map((a, i) => `<button class="btn ${a.primary ? 'primary' : ''}" data-i="${i}">${esc(a.label)}</button>`).join('')}</div>` : ''}</div><button class="icon-btn t-x" data-x aria-label="Close">${icon('x')}</button><i class="t-time"></i>`;
    el.onclick = (e) => {
      const b = e.target.closest('button'); if (!b) { post('showWindow'); post('toast.close'); return; }
      if (b.hasAttribute('data-x')) { post('toast.close'); return; }
      const a = acts[+b.dataset.i]; post('toast.action', { cmd: a.cmd, args: a.args ?? {} });
    };
    left = t.timeout ?? (acts.length ? 20000 : 7000); last = performance.now();
    const bar = el.querySelector('.t-time'), total = left;
    clearInterval(timer);
    timer = setInterval(() => {
      const nowT = performance.now();
      if (!el.matches(':hover')) left -= nowT - last;
      last = nowT;
      bar.style.transform = `scaleX(${Math.max(0, left / total)})`;
      if (left <= 0) { clearInterval(timer); post('toast.close'); }
    }, 100);
    requestAnimationFrame(() => post('toast.size', { height: Math.ceil(el.getBoundingClientRect().height) }));
  }
  web?.addEventListener('message', (e) => { const m = typeof e.data === 'string' ? JSON.parse(e.data) : e.data; if (m?.type === 'toast') show(m.toast); });
  const pick = new URLSearchParams(location.search).get('t');
  if (pick && Mock.toasts[pick]) show({ ...Mock.toasts[pick], timeout: 1e9 });
  else post('toast.ready');
  window.__ready = true;
})();
