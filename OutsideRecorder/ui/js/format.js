// Plain-words helpers of the recorder window: escaping for HTML, times ("in 2 h", "Yesterday · 21:40"), turns, sizes.
// Pure functions: they read no state. The files load in the order of index.html.
'use strict';
const esc = (v) => String(v ?? '').replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[c]);
const attr = (o) => esc(JSON.stringify(o ?? {}));
function span(ms) {
  const m = Math.max(0, Math.round(ms / 60e3));
  if (m < 1) return 'less than a minute';
  if (m < 60) return `${m} min`;
  const h = Math.floor(m / 60), r = m % 60;
  if (h < 24) return r ? `${h} h ${r} min` : `${h} h`;
  const d = Math.round(h / 24); return d === 1 ? '1 day' : `${d} days`;
}
const until = (t) => t - Date.now() <= 0 ? 'any moment now' : 'in ' + span(t - Date.now());
const untilSpan = (t) => `<span data-until="${t}">${until(t)}</span>`;   // goes on counting down by itself (a timer in app.js)
const ago = (t) => Date.now() - t < 60e3 ? 'just now' : span(Date.now() - t) + ' ago';
const day = (t) => {
  const d = new Date(t), today = new Date(); const y = new Date(); y.setDate(today.getDate() - 1);
  if (d.toDateString() === today.toDateString()) return 'Today';
  if (d.toDateString() === y.toDateString()) return 'Yesterday';
  return d.toLocaleDateString('en-GB', { day: 'numeric', month: 'short' });
};
const clock = (t) => new Date(t).toLocaleTimeString('en-GB', { hour: '2-digit', minute: '2-digit' });
function recordingTime(g) {
  const m=/^session-(\d{4})(\d{2})(\d{2})T(\d{2})(\d{2})(\d{2})/.exec(g.id??'');
  return m ? new Date(+m[1],+m[2]-1,+m[3],+m[4],+m[5],+m[6]).getTime() : g.date??g.startedAt??g.lastSaveAt;
}
const recordingLabel = g => Number.isFinite(new Date(recordingTime(g)).getTime()) ? `${g.mod??'Recording'} · ${day(recordingTime(g))} · ${clock(recordingTime(g))}` : `${g.mod??'Current'} recording`;
// "T30–144": the turns a recording holds saves of (the user, 2026-10-31: "say what turns we have recordings of")
const turnsLabel = (g) => g.firstTurn != null && g.lastTurn != null ? (g.firstTurn === g.lastTurn ? `T${g.lastTurn}` : `T${g.firstTurn}–${g.lastTurn}`)
  : g.turns != null ? `${g.turns} ${g.turns === 1 ? 'turn' : 'turns'}` : '';
const turnsGap = (g) => g.firstTurn != null && g.savedTurns && g.lastTurn - g.firstTurn + 1 > g.savedTurns ? g.lastTurn - g.firstTurn + 1 - g.savedTurns : 0;
const kb = (n) => n >= 1048576 ? `${(n / 1048576).toFixed(1)} MB` : `${Math.max(0, Math.round(n / 1024))} KB`;
const cap = (s) => s.charAt(0).toUpperCase() + s.slice(1);
const range = (a, b) => { const d1 = `${day(a)} ${clock(a)}`; return day(a) === day(b) ? `${d1}–${clock(b)}` : `${d1} – ${day(b)} ${clock(b)}`; };
