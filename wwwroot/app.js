// Creative Remix Engine — vanilla JS front end.
'use strict';

const $ = (sel, root = document) => root.querySelector(sel);
const $$ = (sel, root = document) => [...root.querySelectorAll(sel)];
const esc = s => String(s ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
const roleCls = r => 'r-' + String(r || '').toLowerCase();
const rolePill = r => `<span class="role ${roleCls(r)}">${esc(r)}</span>`;
const fmt = sec => {
  if (sec == null || isNaN(sec)) return '–';
  const m = Math.floor(sec / 60), s = sec - m * 60;
  return `${m}:${s < 10 ? '0' : ''}${s.toFixed(1).replace(/\.0$/, '')}`;
};
const range = (a, b) => (a == null ? '' : `${fmt(a)}–${fmt(b)}`);
const bytes = n => n > 1e9 ? (n / 1e9).toFixed(2) + ' GB' : n > 1e6 ? (n / 1e6).toFixed(1) + ' MB' : Math.round(n / 1e3) + ' KB';
const txt = (s, empty = 'None') => s && String(s).trim() ? esc(s) : `<span class="empty">${empty}</span>`;

const S = {
  demo: false, demoData: null, status: null,
  file: null, link: null, youtubeId: null, videoUrl: null, videoInfo: null,
  competitor: null, competitorMeta: null,
  winnerScript: '', winner: null,
  match: null, selected: new Set(),
  mode: 'modifier', count: 5,
  variations: [], approved: new Set(),
  sessionStart: null, step: 1, panel: 'comp'
};

// ---------- API ----------
async function api(path, body, signal) {
  const res = await fetch(path, body === undefined ? { signal } : {
    method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body), signal
  });
  return parse(res.status, await res.text());
}
function parse(status, text) {
  let data = null;
  try { data = text ? JSON.parse(text) : null; } catch { /* not JSON */ }
  if (status >= 200 && status < 300) return data;
  const err = new Error(data?.error || `Request failed (${status}).`);
  err.hint = data?.hint || (status === 413 ? 'The file is too large.' : status === 0 ? 'Is the server running?' : '');
  throw err;
}
function uploadVideo(file, onProgress, force, onXhr) {
  return new Promise((resolve, reject) => {
    const xhr = new XMLHttpRequest();
    const form = new FormData();
    form.append('file', file, file.name);
    xhr.open('POST', '/api/analyze/video' + (force ? '?force=true' : ''));
    onXhr?.(xhr);
    xhr.onabort = () => reject(Object.assign(new Error('Analysis cancelled.'), { cancelled: true }));
    xhr.upload.onprogress = e => e.lengthComputable && onProgress(e.loaded / e.total);
    xhr.upload.onload = () => onProgress(1);
    xhr.onload = () => { try { resolve(parse(xhr.status, xhr.responseText)); } catch (e) { reject(e); } };
    xhr.onerror = () => { const e = new Error('Network error while uploading.'); e.hint = 'Check that the server is running.'; reject(e); };
    xhr.send(form);
  });
}

// ---------- UI helpers ----------
function toast(msg) {
  const t = $('#toast'); t.textContent = msg; t.hidden = false;
  clearTimeout(toast.t); toast.t = setTimeout(() => t.hidden = true, 2200);
}
function showError(el, err, retry) {
  el.innerHTML = `<div class="alert"><b>${esc(err.message)}</b>${err.hint ? `<span class="hint">${esc(err.hint)}</span>` : ''}
    ${retry ? '<div><button class="btn ghost sm" data-retry>Try again</button></div>' : ''}</div>`;
  if (retry) $('[data-retry]', el).onclick = retry;
}
function progress(el, label) {
  const start = Date.now();
  el.innerHTML = `<div class="progress"><div class="row"><span data-label>${esc(label)}</span><span class="mono muted" data-time>0s</span></div><div class="bar"></div></div>`;
  const timer = setInterval(() => { const t = $('[data-time]', el); if (t) t.textContent = Math.round((Date.now() - start) / 1000) + 's'; }, 500);
  return {
    set: l => { const x = $('[data-label]', el); if (x) x.textContent = l; },
    done: () => clearInterval(timer)
  };
}
function busy(btn, on, label) {
  if (on) { btn.dataset.label = btn.textContent; btn.textContent = label; btn.disabled = true; }
  else { btn.textContent = btn.dataset.label || btn.textContent; btn.disabled = false; }
}
function startSession() { S.sessionStart ??= Date.now(); updateKpi(); }

// ---------- Status & stepper ----------
async function loadStatus() {
  try {
    S.status = await api('/api/status');
    const p = (name, x) => `<span class="pill" title="${esc(x.model)}"><span class="dot ${x.configured ? 'on' : ''}"></span>${name} ${x.configured ? 'live' : 'no key'}</span>`;
    const c = S.status.cache;
    $('#status').innerHTML = p('Gemini', S.status.gemini) + p('OpenAI', S.status.openAi) +
      `<span class="pill" title="${esc(c.enabled ? 'Video analyses are cached in PostgreSQL by content hash' : (c.error || ''))}"><span class="dot ${c.enabled ? 'on' : ''}"></span>Cache ${c.enabled ? 'on' : 'off'}</span>`;
    $('#formula').innerHTML = esc(S.status.formula).replace(/(0\.\d+)/g, '<b>$1</b>');
  } catch { $('#status').innerHTML = '<span class="pill"><span class="dot"></span>server offline</span>'; }
}

function available(n) {
  return n === 1 || (n === 2 && (S.competitor || S.winner)) || (n >= 3 && !!S.match) || (n === 4 && !!S.match);
}
function setStep(n) {
  if (!available(n)) return;
  S.step = n;
  $$('.step').forEach((s, i) => s.hidden = i + 1 !== n);
  updateStepper();
  window.scrollTo({ top: 0, behavior: 'smooth' });
  if (n === 2) renderBreakdown();
  if (n === 3) renderMatches();
  if (n === 4) renderGenControls();
}
function updateStepper() {
  const done = { 1: S.competitor && S.winner, 2: !!S.match, 3: S.variations.length > 0, 4: S.approved.size > 0 };
  $$('#stepper button').forEach(b => {
    const n = +b.dataset.step;
    b.disabled = !available(n);
    b.classList.toggle('active', n === S.step);
    b.classList.toggle('done', !!done[n] && n !== S.step);
  });
  $('#toStep2').disabled = !(S.competitor && S.winner);
  $('#exportBtn').disabled = !(S.competitor || S.winner);
}

// ---------- Step 1: video (file or link) ----------
const ytId = url => {
  try {
    const u = new URL(url);
    if (u.hostname === 'youtu.be') return u.pathname.slice(1) || null;
    if (!/(^|\.)youtube\.com$/.test(u.hostname)) return null;
    return u.searchParams.get('v') || u.pathname.match(/\/(shorts|embed|live)\/([\w-]+)/)?.[2] || null;
  } catch { return null; }
};
const hasVideo = () => !!(S.file || S.link);

function resetSource() {
  if (S.file && S.videoUrl) URL.revokeObjectURL(S.videoUrl);
  S.file = S.link = S.youtubeId = S.videoUrl = S.videoInfo = null;
}
function handleFile(file) {
  if (!file) return;
  if (!file.type.startsWith('video/') && !/\.(mp4|mov|webm)$/i.test(file.name)) {
    showError($('#videoStatus'), Object.assign(new Error(`“${file.name}” isn't a video.`), { hint: 'Upload an MP4 ad export.' }));
    return;
  }
  resetSource();
  S.file = file; S.videoUrl = URL.createObjectURL(file);
  sourceChanged();
}
function handleLink(raw) {
  const url = raw.trim();
  try { if (!/^https?:$/.test(new URL(url).protocol)) throw 0; }
  catch {
    showError($('#videoStatus'), Object.assign(new Error('That doesn’t look like a valid link.'),
      { hint: 'Paste a full https:// link to an MP4 file or a YouTube video.' }));
    return;
  }
  resetSource();
  S.link = url; S.youtubeId = ytId(url); S.videoUrl = S.youtubeId ? null : url;
  sourceChanged();
}
function setInputsVisible(show) {
  ['#dropzone', '#linkForm', '#linkHelp', '.or'].forEach(sel => $(sel).hidden = !show);
  $('#videoPanel').hidden = show;
}
function sourceChanged() {
  invalidate('competitor');
  $('#videoStatus').innerHTML = '';
  setInputsVisible(false);
  renderPreview(); renderVideoMeta();
}
function changeVideo() {
  resetSource(); invalidate('competitor');
  $('#videoStatus').innerHTML = ''; $('#linkInput').value = '';
  setInputsVisible(true);
}
function renderPreview() {
  const wrap = $('#previewWrap');
  if (S.youtubeId) {
    wrap.innerHTML = `<iframe class="yt" src="https://www.youtube-nocookie.com/embed/${encodeURIComponent(S.youtubeId)}" allow="autoplay; encrypted-media; picture-in-picture" allowfullscreen title="Video preview"></iframe>`;
    return;
  }
  wrap.innerHTML = '<video id="video1" controls playsinline preload="metadata"></video>';
  const v = $('#video1');
  v.onloadedmetadata = () => { S.videoInfo = { duration: v.duration, w: v.videoWidth, h: v.videoHeight }; renderVideoMeta(); };
  v.onerror = () => {
    if (!S.link) return;
    wrap.innerHTML = '<div class="preview-note">This site doesn’t allow an in-browser preview of the link.<br>You can still analyze it: the server downloads the file and sends it to Gemini.</div>';
  };
  v.src = S.videoUrl;
}
function renderVideoMeta() {
  const i = S.videoInfo;
  let items;
  if (S.file) {
    items = [['File', S.file.name], ['Size', bytes(S.file.size)], ['Duration', i ? fmt(i.duration) : '…'],
      ['Resolution', i ? `${i.w}×${i.h}` : '…'], ['Type', S.file.type || 'video']];
  } else {
    const host = new URL(S.link).hostname.replace(/^www\./, '');
    items = [['Source', host], ['Type', S.youtubeId ? 'YouTube video' : 'Direct video link'],
      ...(S.youtubeId ? [['Handling', 'Gemini reads it directly']]
        : [['Duration', i ? fmt(i.duration) : '–'], ['Resolution', i ? `${i.w}×${i.h}` : '–']])];
  }
  $('#videoMeta').innerHTML = items.map(([k, v]) => `<div><dt>${k}</dt><dd title="${esc(v)}">${esc(v)}</dd></div>`).join('');
}
async function analyzeVideo(force = false) {
  const btn = $('#analyzeVideoBtn'), box = $('#videoStatus');
  if (!hasVideo()) return;
  if (S.status && !S.status.gemini.configured) {
    showError(box, Object.assign(new Error('Gemini API key is not configured.'),
      { hint: 'Set GEMINI_API_KEY and restart the server, or click “Load Demo” to see sample results.' }));
    return;
  }
  busy(btn, true, 'Analyzing…');
  const watching = 'Gemini is watching and listening: segmenting, transcribing, interpreting…';
  const p = progress(box, S.file ? 'Uploading video…' : S.youtubeId ? watching : 'Server is downloading the video, then sending it to Gemini…');
  // Cancel button + a reassuring hint when Gemini is slow (long videos or "high demand" retries).
  let xhr = null;
  const ctrl = new AbortController();
  $('.progress', box).insertAdjacentHTML('beforeend',
    '<div class="row between small"><span class="muted" data-slow></span><button class="link" data-cancel>Cancel</button></div>');
  $('[data-cancel]', box).onclick = () => { xhr?.abort(); ctrl.abort(); };
  const slow = setTimeout(() => { const el = $('[data-slow]', box); if (el) el.textContent =
    'Still working. Long videos or a busy Gemini can take 2–4 minutes; the result is cached, so next time it\'s instant.'; }, 60000);
  try {
    const res = S.file
      ? await uploadVideo(S.file, f => p.set(f < 1 ? `Uploading video… ${Math.round(f * 100)}%` : watching), force, x => xhr = x)
      : await api('/api/analyze/video-url', { url: S.link, force }, ctrl.signal);
    exitDemo(true, 'competitor');
    S.competitor = res.analysis;
    S.competitorMeta = { model: res.model, api: res.api, seconds: res.seconds, raw: res.raw, cached: res.cached, analyzedAt: res.analyzedAt };
    startSession();
    const when = res.analyzedAt ? new Date(res.analyzedAt).toLocaleString([], { dateStyle: 'medium', timeStyle: 'short' }) : '';
    box.innerHTML = `<div class="success">${res.cached
        ? `Loaded instantly from cache · analyzed ${esc(when)} with ${esc(res.model)}`
        : `Analyzed in ${res.seconds}s · ${esc(res.model)}`} · ${res.analysis.segments.length} segments · ${res.analysis.techniques.length} techniques
      <button class="link" id="viewComp">View breakdown</button>${res.cached ? '<button class="link" id="reanalyze">Re-analyze</button>' : ''}</div>`;
    $('#viewComp').onclick = () => { S.panel = 'comp'; setStep(2); };
    if (res.cached) $('#reanalyze').onclick = () => analyzeVideo(true);
    invalidate('match'); prefetchMatches();
  } catch (e) {
    if (e.cancelled || e.name === 'AbortError') box.innerHTML = '<div class="alert"><b>Analysis cancelled.</b></div>';
    else showError(box, e, () => analyzeVideo(force));
  }
  finally { clearTimeout(slow); p.done(); busy(btn, false); updateStepper(); }
}

// ---------- Step 1: script ----------
function updateScriptStats() {
  const words = ($('#scriptInput').value.match(/\S+/g) || []).length;
  $('#scriptStats').textContent = `${words} words · ~${Math.round(words / 2.6)}s spoken`;
}
async function analyzeScript() {
  const btn = $('#analyzeScriptBtn'), box = $('#scriptStatus');
  const script = $('#scriptInput').value.trim();
  if (script.length < 40) {
    showError(box, Object.assign(new Error('The script is too short.'), { hint: 'Paste the full script, or click “Use sample script”.' }));
    return;
  }
  busy(btn, true, 'Analyzing…');
  const p = progress(box, 'GPT is segmenting the script and finding what to preserve…');
  try {
    const res = await api('/api/analyze/script', { script });
    exitDemo(true, 'winner');
    S.winnerScript = script; S.winner = res;
    startSession();
    box.innerHTML = `<div class="success">${res.segments.length} segments · ${res.preserve.length} elements to preserve
      <button class="link" id="viewWin">View breakdown</button></div>`;
    $('#viewWin').onclick = () => { S.panel = 'win'; setStep(2); };
    invalidate('match'); prefetchMatches();
  } catch (e) { showError(box, e, analyzeScript); }
  finally { p.done(); busy(btn, false); updateStepper(); }
}
async function useSample() {
  const d = await getDemo();
  $('#scriptInput').value = d.winnerScript; updateScriptStats();
  toast('Sample script loaded. Click “Analyze script” to run it live.');
}

function invalidate(what) {
  S.ver = (S.ver || 0) + 1; // any in-flight prefetch for older inputs is ignored
  S.matchPromise = null;
  if (what === 'competitor') { S.competitor = null; S.competitorMeta = null; }
  if (what === 'competitor' || what === 'match') { S.match = null; S.selected.clear(); S.variations = []; S.approved.clear(); }
  updateStepper(); updateKpi();
}

// ---------- Demo ----------
async function getDemo() { return S.demoData ??= await api('/api/demo'); }
async function loadDemo() {
  const btn = $('#demoBtn');
  busy(btn, true, 'Loading…');
  try {
    const d = await getDemo();
    S.demo = true;
    S.competitor = d.competitor; S.competitorMeta = { model: 'sample', api: 'demo', seconds: 0, raw: JSON.stringify(d.competitor, null, 2) };
    S.winnerScript = d.winnerScript; S.winner = d.winner;
    $('#scriptInput').value = d.winnerScript; updateScriptStats();
    S.match = null; S.selected.clear(); S.variations = []; S.approved.clear();
    $('#demoBanner').hidden = false;
    $('#scriptStatus').innerHTML = '<div class="success">Sample analysis loaded <span class="badge sample">Sample</span></div>';
    if (!hasVideo()) $('#videoStatus').innerHTML = '<div class="success">Sample competitor analysis loaded (no video file) <span class="badge sample">Sample</span></div>';
    startSession(); S.panel = 'comp'; updateStepper(); setStep(2);
  } catch (e) { showError($('#videoStatus'), e); }
  finally { busy(btn, false); }
}
function exitDemo(keepState, livePart) {
  if (!S.demo) return;
  S.demo = false; $('#demoBanner').hidden = true;
  if (keepState) {
    // A live analysis replaces its own half; the other half was sample data, so drop it rather than mix the two.
    if (livePart === 'competitor') { S.winner = null; $('#scriptStatus').innerHTML = ''; }
    else { S.competitor = S.competitorMeta = null; $('#videoStatus').innerHTML = ''; }
    S.match = null; S.selected.clear(); S.variations = []; S.approved.clear();
    toast(`Switched to live mode. Sample ${livePart === 'competitor' ? 'script analysis' : 'video analysis'} cleared; analyze it live to continue.`);
  } else {
    S.competitor = S.competitorMeta = S.winner = S.match = null; S.selected.clear(); S.variations = []; S.approved.clear();
    $('#scriptStatus').innerHTML = ''; $('#videoStatus').innerHTML = '';
    setStep(1);
  }
  updateStepper(); updateKpi();
}

// ---------- Step 2: breakdown ----------
function renderBreakdown() {
  $$('.seg-toggle button').forEach(b => b.classList.toggle('active', b.dataset.panel === S.panel));
  $('#compPanel').hidden = S.panel !== 'comp';
  $('#winPanel').hidden = S.panel !== 'win';
  $('#compPanel').innerHTML = S.competitor ? analysisView(S.competitor, 'comp') : emptyPanel('competitor video', 1);
  $('#winPanel').innerHTML = S.winner ? analysisView(S.winner, 'win') : emptyPanel('winning script', 1);
  $('#toStep3').disabled = !(S.competitor && S.winner);
  wireBreakdown();
}
function emptyPanel(what) {
  return `<div class="card"><p class="muted">The ${what} hasn't been analyzed yet. Go back to <button class="link" onclick="setStep(1)">Creative inputs</button>.</p></div>`;
}
function analysisView(a, kind) {
  const isComp = kind === 'comp';
  const sample = S.demo ? '<span class="badge sample">Sample</span>' : '';
  const dur = a.durationSec || Math.max(...a.segments.map(s => s.endSec || 0)) || 1;
  const media = !isComp ? ''
    : S.demo || !hasVideo()
      ? `<div class="video-ph"><div><b>No video attached</b><br>This is a sample analysis of a fictional 34s UGC ad.<br>Drop a real MP4 or paste a link in Step 1 to analyze it live with Gemini.</div></div>`
      : S.youtubeId
        ? `<div class="video-wrap"><iframe class="yt" id="yt2" src="https://www.youtube-nocookie.com/embed/${encodeURIComponent(S.youtubeId)}" allow="autoplay; encrypted-media" allowfullscreen title="Competitor video"></iframe></div>`
        : `<div class="video-wrap"><video id="video2" src="${esc(S.videoUrl)}" controls playsinline></video></div>`;
  const meta = isComp && S.competitorMeta && !S.demo
    ? `<div class="muted small">Parsed by <b>${esc(S.competitorMeta.model)}</b> via ${esc(S.competitorMeta.api)} in ${S.competitorMeta.seconds}s${S.competitorMeta.cached ? ' · loaded from cache' : ''}</div>` : '';

  return `
  <div class="breakdown">
    <div class="sticky">
      ${media}
      <div class="card timeline-card">
        <div class="row between"><h3>${isComp ? 'Segment timeline' : 'Script structure'}</h3>${sample}</div>
        ${timeline(a, dur, kind)}
        ${isComp ? '' : '<div class="muted small">Timings estimated from speaking pace (~2.6 words/sec).</div>'}
      </div>
    </div>
    <div class="stack">
      <div class="card summary">
        <div class="eyebrow">${isComp ? 'Competitor creative' : 'Our winner'} · ${esc(a.productCategory)}</div>
        <div class="title">${esc(a.title)}</div>
        <p>${esc(a.summary)}</p>
        <dl class="kv">
          <div><dt>Primary angle</dt><dd>${esc(a.primaryAngle)}</dd></div>
          <div><dt>Storytelling pattern</dt><dd>${esc(a.storytellingPattern)}</dd></div>
          <div><dt>Audience</dt><dd>${esc(a.targetAudience)}</dd></div>
        </dl>
        ${meta}
      </div>
      ${a.segments.map(s => segmentCard(s, kind)).join('')}
    </div>
  </div>

  ${!isComp && a.preserve?.length ? `
  <div class="section-title"><h3>Keep these: what makes it win</h3></div>
  <div class="tech-grid">${a.preserve.map(p => `
    <div class="tech"><div class="row"><span class="badge">${esc(p.segmentId)}</span><span class="tech-name">${esc(p.element)}</span></div><p>${esc(p.reason)}</p></div>`).join('')}
  </div>` : ''}

  <div class="section-title"><h3>${isComp ? 'Reusable techniques' : 'Techniques it already uses'}</h3><span class="muted small">${isComp ? 'Product-agnostic and ready to adapt' : ''}</span></div>
  <div class="tech-grid">${a.techniques.map(t => `
    <div class="tech">
      <div class="row"><span class="badge">${esc(t.id)}</span><span class="badge">${esc(t.category)}</span>
        ${t.startSec != null ? `<button class="ts" data-seek="${t.startSec}" data-kind="${kind}">${range(t.startSec, t.endSec)}</button>` : ''}</div>
      <div class="tech-name">${esc(t.name)}</div>
      <p>${esc(t.description)}</p>
      <div><div class="lbl">How to reuse</div><p>${esc(t.howToReuse)}</p></div>
      <div class="evidence">${esc(t.evidence)}</div>
    </div>`).join('')}
  </div>

  ${a.productBenefits?.length ? `<details class="box"><summary>${isComp ? 'Benefits the competitor claims (observed)' : 'Product benefits stated'}</summary><div><ul>${a.productBenefits.map(b => `<li>${esc(b)}</li>`).join('')}</ul></div></details>` : ''}
  ${isComp && S.competitorMeta ? `<details class="box"><summary>${S.demo ? 'Sample analysis JSON' : 'Raw Gemini output: exactly what the model returned'}</summary><div><pre class="raw">${esc(pretty(S.competitorMeta.raw))}</pre></div></details>` : ''}
  ${!isComp ? `<details class="box"><summary>Script analysis JSON</summary><div><pre class="raw">${esc(JSON.stringify(a, null, 2))}</pre></div></details>` : ''}`;
}
function pretty(raw) { try { return JSON.stringify(JSON.parse(raw), null, 2); } catch { return raw; } }

function timeline(a, dur, kind) {
  const blocks = a.segments.map(s => {
    const w = Math.max(((s.endSec ?? 0) - (s.startSec ?? 0)) / dur * 100, 2);
    return `<button class="tl-block ${roleCls(s.role)}" style="flex:${w} 0 0;--c:var(--${s.role.toLowerCase()})" data-seg="${s.id}" data-kind="${kind}"
      data-seek="${s.startSec ?? ''}" title="${esc(s.role)} · ${range(s.startSec, s.endSec)}">${esc(s.role === 'Demonstration' ? 'Demo' : s.role)}</button>`;
  }).join('');
  return `<div class="timeline"><div class="tl-track">${blocks}${kind === 'comp' ? '<div class="tl-head" id="tlHead" style="left:0"></div>' : ''}</div>
    <div class="tl-scale"><span>0:00</span><span>${fmt(dur / 2)}</span><span>${fmt(dur)}</span></div></div>`;
}
function segmentCard(s, kind) {
  const o = s.observed || {}, i = s.interpretation || {};
  return `
  <article class="segment" id="seg-${kind}-${esc(s.id)}">
    <div class="seg-top">
      ${rolePill(s.role)}
      ${s.startSec != null ? `<button class="ts" data-seek="${s.startSec}" data-kind="${kind}">${range(s.startSec, s.endSec)}</button>` : ''}
      <span class="seg-id">${esc(s.id)}</span>
    </div>
    <div class="observed">
      <div><div class="lbl">Spoken</div>${o.spokenText ? `<div class="quote">“${esc(o.spokenText)}”</div>` : txt('', 'No voice-over')}</div>
      <div><div class="lbl">On-screen text</div>${o.onScreenText ? `<div class="ost">${esc(o.onScreenText)}</div>` : txt('', 'None')}</div>
      <div><div class="lbl">Visual</div><div class="small">${txt(o.visualDescription, 'Not specified')}</div>
        ${o.audioNotes ? `<div class="lbl" style="margin-top:8px">Audio</div><div class="small">${esc(o.audioNotes)}</div>` : ''}</div>
    </div>
    <details class="insight">
      <summary><span class="ai-tag">AI INTERPRETATION</span> ${esc(i.mechanism)} <span class="conf">confidence ${Math.round((i.confidence ?? 0) * 100)}%</span></summary>
      <div class="insight-grid">
        <div><div class="lbl">Advertising angle</div>${txt(i.angle)}</div>
        <div><div class="lbl">Emotional trigger</div>${txt(i.emotionalTrigger)}</div>
        <div><div class="lbl">Storytelling pattern</div>${txt(i.storytellingPattern)}</div>
        <div><div class="lbl">Persuasion mechanism</div>${txt(i.mechanism)}</div>
        <div class="wide"><div class="lbl">Reusable pattern</div>${txt(i.reusablePattern)}</div>
        <div class="wide"><div class="lbl">Evidence</div><div class="evidence">${txt(i.evidence)}</div></div>
      </div>
    </details>
  </article>`;
}
function wireBreakdown() {
  const v = $('#video2');
  $$('[data-seek]').forEach(el => el.addEventListener('click', () => {
    const kind = el.dataset.kind, seg = el.dataset.seg;
    if (seg) highlight(kind, seg, true);
    if (kind !== 'comp' || el.dataset.seek === '') return;
    if (v) { v.currentTime = +el.dataset.seek + 0.05; v.play().catch(() => { }); }
    const yt = $('#yt2');
    if (yt) yt.src = `https://www.youtube-nocookie.com/embed/${encodeURIComponent(S.youtubeId)}?start=${Math.floor(+el.dataset.seek)}&autoplay=1`;
  }));
  if (v && S.competitor) {
    const dur = S.competitor.durationSec || Math.max(...S.competitor.segments.map(s => s.endSec || 0));
    v.ontimeupdate = () => {
      const t = v.currentTime, head = $('#tlHead');
      if (head) head.style.left = Math.min(t / (v.duration || dur) * 100, 100) + '%';
      const cur = S.competitor.segments.find(s => t >= s.startSec && t < s.endSec);
      if (cur && cur.id !== wireBreakdown.active) { wireBreakdown.active = cur.id; highlight('comp', cur.id, false); }
    };
  }
}
function highlight(kind, id, scroll) {
  $$(`.segment[id^="seg-${kind}-"]`).forEach(e => e.classList.toggle('active', e.id === `seg-${kind}-${id}`));
  $$(`.tl-block[data-kind="${kind}"]`).forEach(e => e.classList.toggle('active', e.dataset.seg === id));
  if (scroll) $(`#seg-${kind}-${CSS.escape(id)}`)?.scrollIntoView({ behavior: 'smooth', block: 'start' });
}

// ---------- Step 3: matching ----------
/** Starts matching in the background as soon as both analyses exist, so Step 3 is ready when the user gets there. */
function prefetchMatches() {
  if (S.demo || S.match || S.matchPromise || !(S.competitor && S.winner)) return;
  const ver = S.ver;
  S.matchPromise = api('/api/match', { competitor: S.competitor, winner: S.winner }).then(res => {
    if (ver !== S.ver) return null;
    setMatch(res); toast('Technique matches are ready');
    return res;
  });
  S.matchPromise.catch(() => { if (ver === S.ver) S.matchPromise = null; });
}
function setMatch(res) {
  S.match = res;
  // Pre-select only the top 3, so the user chooses consciously rather than unticking a long list.
  S.selected = new Set(res.matches.filter(m => m.accepted).slice(0, 3).map(m => m.candidate.id));
  updateStepper();
}
async function findMatches() {
  const btn = $('#toStep3'), box = $('#matchStatus');
  if (!(S.competitor && S.winner)) return;
  if (S.match) return setStep(3);
  busy(btn, true, 'Matching…');
  const p = progress(box, S.demo ? 'Loading sample matches…' : 'Scoring competitor techniques against our script…');
  try {
    if (S.demo) { await new Promise(r => setTimeout(r, 400)); setMatch((await getDemo()).match); }
    else if (S.matchPromise) await S.matchPromise;
    if (!S.match) setMatch(await api('/api/match', { competitor: S.competitor, winner: S.winner }));
    box.innerHTML = '';
    setStep(3);
  } catch (e) { showError(box, e, findMatches); }
  finally { p.done(); busy(btn, false); updateStepper(); }
}

const CRIT = [['role', 'Role', 0.25], ['pain', 'Pain', 0.20], ['mechanism', 'Mechanism', 0.25], ['productFit', 'Product fit', 0.20], ['novelty', 'Novelty', 0.10]];
const seg = (a, id) => a?.segments.find(s => s.id === id);
const techOf = c => S.competitor.techniques.find(t => t.id === c.competitorTechniqueId);
const clipOf = c => {
  const t = techOf(c), sg = seg(S.competitor, c.competitorSegmentId);
  return { start: t?.startSec ?? sg?.startSec ?? 0, end: t?.endSec ?? sg?.endSec ?? null };
};

function renderMatches() {
  if (!S.match) return;
  const acc = S.match.matches.filter(m => m.accepted), rej = S.match.matches.filter(m => !m.accepted);
  // Group by the winner segment each technique would change, in script order.
  const groups = (S.winner.segments || []).map(w => ({ w, items: acc.filter(m => m.candidate.winnerSegmentId === w.id) }))
    .filter(g => g.items.length);
  $('#matchGroups').innerHTML = groups.length ? groups.map(({ w, items }) => `
    <section class="group">
      <div class="group-head">${rolePill(w.role)}<span class="excerpt">“${esc(w.observed.spokenText || w.observed.onScreenText)}”</span>
        <span class="n">${items.length} option${items.length > 1 ? 's' : ''}</span></div>
      ${items.map(pickCard).join('')}
    </section>`).join('')
    : '<div class="card muted">No compatible techniques. See what was ruled out below.</div>';

  $('#rejectedList').innerHTML = rej.map(m => `<div class="ruled"><span class="x">✕</span><b>${esc(m.candidate.techniqueName)}</b>
    <span class="why">${esc(m.rejectionReasons[0] || '')}${m.candidate.riskNotes ? ' · ' + esc(m.candidate.riskNotes) : ''}</span></div>`).join('');
  $('#rejCount').textContent = rej.length;
  $('#rejectedWrap').hidden = !rej.length;

  $$('#matchGroups .pick').forEach(el => el.addEventListener('click', e => {
    if (e.target.closest('.thumb.playable')) return playClip(el.dataset.id);
    if (e.target.closest('[data-details]')) { const d = $('.pick-details', el); d.hidden = !d.hidden; return; }
    if (e.target.closest('.pick-details')) return;
    const id = el.dataset.id;
    S.selected.has(id) ? S.selected.delete(id) : S.selected.add(id);
    syncSelection();
  }));
  syncSelection();
  paintThumbs();
}

function pickCard(m) {
  const c = m.candidate, t = techOf(c), clip = clipOf(c), comp = seg(S.competitor, c.competitorSegmentId);
  const cls = m.score >= 75 ? 'hi' : m.score >= 60 ? 'mid' : 'lo';
  const playable = !S.demo && hasVideo();
  const barsTitle = CRIT.map(([k, l]) => `${l} ${c.criteria[k].score}/10`).join(' · ');
  return `
  <article class="pick" data-id="${esc(c.id)}">
    <button class="check" role="checkbox" aria-label="Use this technique"><svg viewBox="0 0 24 24"><path d="M5 12l5 5L20 7"/></svg></button>
    <button class="thumb ${playable ? 'playable' : ''}" style="--c:var(--${(comp?.role || 'hook').toLowerCase()})" data-t="${clip.start}"
      title="${playable ? 'Play this moment' : 'Competitor moment'}">
      <span class="cat">${esc(t?.category || comp?.role || '')}</span>
      <span class="ts-badge">${fmt(clip.start)}</span>${playable ? '<span class="play">▶</span>' : ''}
    </button>
    <div class="pick-main">
      <div class="pick-title">${esc(c.techniqueName)}${c.expectedImpact ? `<span class="impact">↑ ${esc(c.expectedImpact)}</span>` : ''}</div>
      <div class="pick-change">${esc(c.whatToChange)}</div>
      <div class="pick-meta">
        <span>Keeps: ${esc(c.whatToKeep)}</span>
        ${c.riskNotes ? `<span class="warn">⚠ ${esc(c.riskNotes)}</span>` : ''}
        <button class="link" data-details>Details</button>
      </div>
    </div>
    <div class="pick-score" title="${esc(barsTitle)}"><b class="${cls}">${m.score}</b>
      <span class="bars">${CRIT.map(([k]) => `<i style="height:${Math.max(c.criteria[k].score * 10, 8)}%"></i>`).join('')}</span></div>
    <div class="pick-details" hidden>
      <div><div class="lbl">Why it works</div><p>${esc(c.whyTransferable)}</p></div>
      <div><div class="lbl">Hypothesis</div><p>${esc(c.hypothesis)}</p></div>
      <div><div class="lbl">Source</div><p>${esc(c.competitorTechniqueId)} · ${comp ? esc(comp.role) : ''} ${range(clip.start, clip.end)}</p></div>
      <div class="wide crit">${CRIT.map(([k, label, w]) => `<div class="crit-row"><span>${label} <span class="muted">×${w}</span></span>
        <span class="cbar"><i style="width:${c.criteria[k].score * 10}%"></i></span><span class="pts">${c.criteria[k].score}/10</span>
        <span class="reason">${esc(c.criteria[k].reason)}</span></div>`).join('')}</div>
    </div>
  </article>`;
}

function syncSelection() {
  $$('#matchGroups .pick').forEach(el => {
    const on = S.selected.has(el.dataset.id);
    el.classList.toggle('selected', on);
    $('.check', el).setAttribute('aria-checked', on);
  });
  const sel = selectedMatches();
  const picked = new Set(sel.map(c => c.winnerSegmentId));
  const roles = [...new Set(S.winner.segments.filter(w => picked.has(w.id)).map(w => w.role))]; // in script order
  $('#selCount').textContent = sel.length;
  $('#selSummary').textContent = roles.length ? `· will change ${roles.join(', ')}` : '· pick at least one';
  $('#toStep4').disabled = sel.length === 0;
}
function quickPick(kind) {
  const acc = S.match.matches.filter(m => m.accepted);
  const pick = kind === 'top3' ? acc.slice(0, 3) : kind === 'strong' ? acc.filter(m => m.score >= 75) : [];
  S.selected = new Set(pick.map(m => m.candidate.id));
  syncSelection();
}

/** Draws the competitor frame for each card: frames from the local/linked video, or the YouTube thumbnail. */
async function paintThumbs() {
  const thumbs = $$('#matchGroups .thumb');
  if (S.demo || !hasVideo() || !thumbs.length) return;
  if (S.youtubeId) {
    thumbs.forEach(t => t.insertAdjacentHTML('afterbegin', `<img alt="" src="https://i.ytimg.com/vi/${encodeURIComponent(S.youtubeId)}/mqdefault.jpg">`));
    return;
  }
  const v = document.createElement('video');
  v.muted = true; v.preload = 'auto'; v.src = S.videoUrl;
  try { await new Promise((ok, fail) => { v.onloadeddata = ok; v.onerror = fail; }); } catch { return; }
  for (const t of thumbs) {
    if (!t.isConnected) return;
    await new Promise(ok => { v.onseeked = ok; v.currentTime = Math.min(+t.dataset.t + 0.3, (v.duration || 1) - 0.1); });
    const c = document.createElement('canvas');
    c.width = 208; c.height = 130;
    const ctx = c.getContext('2d'), r = Math.max(c.width / v.videoWidth, c.height / v.videoHeight);
    ctx.drawImage(v, (c.width - v.videoWidth * r) / 2, (c.height - v.videoHeight * r) / 2, v.videoWidth * r, v.videoHeight * r);
    t.prepend(c);
  }
}

function playClip(id) {
  const c = S.match.matches.find(m => m.candidate.id === id)?.candidate;
  if (!c) return;
  const { start, end } = clipOf(c);
  $('#clipTitle').textContent = `${c.techniqueName} · ${range(start, end)}`;
  if (S.youtubeId) {
    $('#clipBody').innerHTML = `<iframe src="https://www.youtube-nocookie.com/embed/${encodeURIComponent(S.youtubeId)}?start=${Math.floor(start)}${end ? `&end=${Math.ceil(end)}` : ''}&autoplay=1" allow="autoplay; encrypted-media" title="Competitor clip"></iframe>`;
  } else {
    $('#clipBody').innerHTML = '<video controls playsinline autoplay></video>';
    const v = $('#clipBody video');
    v.src = S.videoUrl;
    v.onloadedmetadata = () => { v.currentTime = start; v.play().catch(() => { }); };
    v.ontimeupdate = () => { if (end && v.currentTime >= end) v.pause(); };
  }
  $('#clipModal').hidden = false;
}
function closeClip() { $('#clipModal').hidden = true; $('#clipBody').innerHTML = ''; }

// ---------- Step 4: generation ----------
function renderGenControls() {
  $$('.mode').forEach(b => b.classList.toggle('active', b.dataset.mode === S.mode));
  $('#countInput').value = S.count;
  const sel = selectedMatches();
  $('#selectedChips').innerHTML = `<span class="muted">Influenced by:</span> ` +
    (sel.length ? sel.map(c => `<span class="badge">${esc(c.techniqueName)} → ${esc(c.winnerSegmentId)}</span>`).join('')
      : '<span class="badge warn">No techniques selected. Go back to Step 3.</span>');
  $('#generateBtn').disabled = !sel.length;
  renderVariations();
}
const selectedMatches = () => (S.match?.matches || []).filter(m => S.selected.has(m.candidate.id)).map(m => m.candidate);

async function generate() {
  const btn = $('#generateBtn'), box = $('#genStatus');
  S.count = Math.max(1, Math.min(10, +$('#countInput').value || 5));
  busy(btn, true, 'Generating…');
  const p = progress(box, S.demo ? 'Loading sample variations…'
    : `Writing ${S.count} ${S.mode === 'creator' ? 'original concept(s)' : 'variations'} in parallel, each with its own creative direction…`);
  try {
    let vs;
    if (S.demo) {
      await new Promise(r => setTimeout(r, 600));
      const pool = (await getDemo()).variations[S.mode];
      vs = pool.slice(0, S.count);
      if (S.count > pool.length) toast(`The demo includes ${pool.length} sample ${S.mode} variation(s).`);
    } else {
      const res = await api('/api/generate', {
        mode: S.mode, count: S.count, winnerScript: S.winnerScript, winner: S.winner,
        competitor: S.competitor, selectedMatches: selectedMatches()
      });
      vs = res.variations;
      if (res.failed) toast(`${res.failed} of ${S.count} variations failed; showing the rest.`);
    }
    const batch = (generate.batch = (generate.batch || 0) + 1);
    vs.forEach(v => { v._mode = S.mode; v._key = `${batch}-${S.mode}-${v.id}`; });
    S.variations = [...vs, ...S.variations]; // newest batch on top; earlier batches (and their approvals) stay
    box.innerHTML = '';
    renderVariations();
    $('#variations').scrollIntoView({ behavior: 'smooth', block: 'start' });
  } catch (e) { showError(box, e, generate); }
  finally { p.done(); busy(btn, false); updateStepper(); }
}
const vKey = v => v._key;

function renderVariations() {
  $('#variations').innerHTML = S.variations.map(variationCard).join('');
  $$('#variations .variation').forEach(card => {
    const v = S.variations.find(x => vKey(x) === card.dataset.key);
    $$('.v-tabs button', card).forEach(b => b.onclick = () => {
      $$('.v-tabs button', card).forEach(x => x.classList.toggle('active', x === b));
      $$('.v-pane', card).forEach(p => p.hidden = p.dataset.pane !== b.dataset.pane);
    });
    $('[data-copy]', card).onclick = () => copyVariation(v);
    $('[data-approve]', card).onclick = () => {
      const k = vKey(v);
      S.approved.has(k) ? S.approved.delete(k) : S.approved.add(k);
      renderVariations(); updateKpi(); updateStepper();
    };
  });
}
function variationCard(v) {
  const k = vKey(v), approved = S.approved.has(k), warns = v.complianceWarnings || [];
  const original = (S.winnerScript || '').toLowerCase().replace(/\s+/g, ' ');
  const isChanged = s => s.voiceOver && !original.includes(s.voiceOver.toLowerCase().replace(/\s+/g, ' ').slice(0, 40));
  return `
  <article class="card variation" data-key="${esc(k)}">
    <div class="v-head">
      <div class="v-title">
        <span class="v-num">${esc(v.id)}</span><h2>${esc(v.name)}</h2>
        <span class="badge">${v._mode === 'creator' ? 'Creator · new concept' : 'Modifier'}</span>
        <span class="badge">~${v.estimatedDurationSec}s</span>
        ${S.demo ? '<span class="badge sample">Sample</span>' : ''}
        ${warns.length ? `<span class="badge warn">⚠ ${warns.length} compliance flag${warns.length > 1 ? 's' : ''}</span>` : '<span class="badge ok">✓ Compliance check passed</span>'}
      </div>
      <div class="v-angle"><b>Angle:</b> ${esc(v.angle)}</div>
      <div class="v-hook">${esc(v.hook)}</div>
      <div class="insp">${v.inspiredBy.map(x => `<span class="badge" title="${esc(x.howApplied)}">Inspired by <b>${esc(x.techniqueName)}</b> <span class="mono muted">${esc(x.competitorTimestamp)}</span></span>`).join('')}</div>
    </div>
    <div class="v-tabs">
      <button class="active" data-pane="scenes">Scenes</button>
      <button data-pane="compare">Original vs. modified</button>
      <button data-pane="script">Full script</button>
    </div>
    <div class="v-pane" data-pane="scenes">
      <div class="scenes">
        <div class="scene headrow"><div>Scene</div><div>Voice-over</div><div>On-screen text</div><div>Visual direction</div></div>
        ${v.scenes.map(s => `<div class="scene">
          <div class="when"><span class="mono muted">${s.number} · ${esc(s.timing)}</span>${rolePill(s.role)}</div>
          <div class="vo">${s.voiceOver ? `“${esc(s.voiceOver)}”` : txt('', 'No VO')}</div>
          <div class="os">${txt(s.onScreenText, 'None')}</div>
          <div class="vd">${esc(s.visualDirection)}</div></div>`).join('')}
      </div>
    </div>
    <div class="v-pane" data-pane="compare" hidden>
      <div class="compare">
        <div><h4>Original winner</h4>${(S.winner?.segments || []).map(s => `<div class="cmp-item">${rolePill(s.role)}<div>“${esc(s.observed.spokenText)}”</div><div class="os">${esc(s.observed.onScreenText)}</div></div>`).join('')}</div>
        <div><h4>${esc(v.name)}</h4>${v.scenes.map(s => `<div class="cmp-item ${isChanged(s) ? 'changed' : ''}">${rolePill(s.role)}<div>“${esc(s.voiceOver)}”</div><div class="os">${esc(s.onScreenText)}</div></div>`).join('')}</div>
      </div>
      <p class="muted small" style="margin-top:8px">Highlighted scenes contain new or rewritten voice-over.</p>
    </div>
    <div class="v-pane" data-pane="script" hidden><pre class="script">${esc(v.fullScript)}</pre></div>
    ${warns.length ? `<ul class="warns v-warns">${warns.map(w => `<li>${esc(w)}</li>`).join('')}</ul>` : ''}
    <div class="v-foot">
      <span class="muted small">${v.inspiredBy.length} technique${v.inspiredBy.length === 1 ? '' : 's'} applied · ${v.scenes.length} scenes</span>
      <div class="row gap">
        <button class="btn ghost sm" data-copy>Copy brief</button>
        <button class="btn sm ${approved ? 'on' : 'ghost'}" data-approve>${approved ? '✓ Approved' : 'Approve'}</button>
      </div>
    </div>
  </article>`;
}
function briefText(v) {
  return [`${v.name.toUpperCase()} (${v.id}): ${v._mode === 'creator' ? 'Creator concept' : 'Modifier'}`,
    `Angle: ${v.angle}`, `Hook: ${v.hook}`,
    `Inspired by: ${v.inspiredBy.map(x => `${x.techniqueName} (competitor ${x.competitorTimestamp})`).join('; ')}`, '',
    v.fullScript].join('\n');
}
async function copyVariation(v) {
  try { await navigator.clipboard.writeText(briefText(v)); toast(`Copied “${v.name}” brief`); }
  catch { toast('Clipboard is unavailable in this browser.'); }
}

// ---------- KPI & export ----------
function updateKpi() {
  $('#kpiApproved').textContent = S.approved.size;
  if (!S.sessionStart || !S.approved.size) { $('#kpiRate').textContent = '—'; return; }
  const hours = Math.max((Date.now() - S.sessionStart) / 3.6e6, 1 / 60);
  $('#kpiRate').textContent = (S.approved.size / hours).toFixed(1);
}
function exportJson() {
  const data = {
    exportedAt: new Date().toISOString(), mode: S.demo ? 'demo-sample' : 'live',
    sessionMinutes: S.sessionStart ? +((Date.now() - S.sessionStart) / 60000).toFixed(1) : 0,
    competitor: S.competitor, competitorMeta: S.competitorMeta && { ...S.competitorMeta, raw: undefined },
    winnerScript: S.winnerScript, winner: S.winner,
    matches: S.match?.matches.map(m => ({ ...m, selected: S.selected.has(m.candidate.id) })), formula: S.match?.formula,
    variations: S.variations.map(v => ({ ...v, mode: v._mode, approved: S.approved.has(vKey(v)) }))
  };
  const a = document.createElement('a');
  a.href = URL.createObjectURL(new Blob([JSON.stringify(data, null, 2)], { type: 'application/json' }));
  a.download = `socklock-remix-${new Date().toISOString().slice(0, 16).replace(/[:T]/g, '-')}.json`;
  a.click(); setTimeout(() => URL.revokeObjectURL(a.href), 1000);
}

// ---------- Wiring ----------
function init() {
  const dz = $('#dropzone'), fi = $('#fileInput');
  ['dragenter', 'dragover'].forEach(ev => dz.addEventListener(ev, e => { e.preventDefault(); dz.classList.add('over'); }));
  ['dragleave', 'drop'].forEach(ev => dz.addEventListener(ev, e => { e.preventDefault(); dz.classList.remove('over'); }));
  dz.addEventListener('drop', e => handleFile(e.dataTransfer.files[0]));
  // Allow dropping a video anywhere on Step 1.
  $('#step1').addEventListener('dragover', e => e.preventDefault());
  $('#step1').addEventListener('drop', e => { e.preventDefault(); if (e.dataTransfer.files[0]) handleFile(e.dataTransfer.files[0]); });
  fi.addEventListener('change', () => handleFile(fi.files[0]));
  $('#replaceBtn').onclick = changeVideo;
  $('#linkForm').addEventListener('submit', e => { e.preventDefault(); handleLink($('#linkInput').value); });
  $('#linkInput').addEventListener('paste', () => setTimeout(() => {
    const v = $('#linkInput').value.trim();
    if (/^https?:\/\//.test(v)) handleLink(v);
  }, 0));
  $('#analyzeVideoBtn').onclick = () => analyzeVideo(false);

  $('#scriptInput').addEventListener('input', () => { updateScriptStats(); });
  $('#analyzeScriptBtn').onclick = analyzeScript;
  $('#sampleScriptBtn').onclick = useSample;

  $('#toStep2').onclick = () => setStep(2);
  $('#toStep3').onclick = findMatches;
  $('#toStep4').onclick = () => setStep(4);
  $$('#stepper button').forEach(b => b.onclick = () => setStep(+b.dataset.step));
  $$('.seg-toggle button').forEach(b => b.onclick = () => { S.panel = b.dataset.panel; renderBreakdown(); });

  $('#pickTop3').onclick = () => quickPick('top3');
  $('#pickStrong').onclick = () => quickPick('strong');
  $('#pickNone').onclick = () => quickPick('none');
  $('#clipClose').onclick = closeClip;
  $('#clipModal').addEventListener('click', e => { if (e.target.id === 'clipModal') closeClip(); });
  document.addEventListener('keydown', e => { if (e.key === 'Escape' && !$('#clipModal').hidden) closeClip(); });

  $$('.mode').forEach(b => b.onclick = () => { S.mode = b.dataset.mode; renderGenControls(); });
  $('#cntMinus').onclick = () => { $('#countInput').value = Math.max(1, +$('#countInput').value - 1); };
  $('#cntPlus').onclick = () => { $('#countInput').value = Math.min(10, +$('#countInput').value + 1); };
  $('#generateBtn').onclick = generate;

  $('#demoBtn').onclick = loadDemo;
  $('#exitDemoBtn').onclick = () => exitDemo(false);
  $('#exportBtn').onclick = exportJson;
  setInterval(updateKpi, 30000);

  updateScriptStats(); updateStepper(); loadStatus();
}
init();
