const $ = id => document.getElementById(id);
const docsApi = '/api/admin/docs';
const promptsApi = '/api/admin/prompts';
const fmtTime = s => new Date(s).toLocaleString('vi-VN');

async function call(url, options) {
  const res = await fetch(url, options);
  if (!res.ok) throw new Error((await res.text()) || res.statusText);
  return res.json();
}

const send = (url, method, body) => call(url, {
  method,
  headers: { 'Content-Type': 'application/json' },
  body: JSON.stringify(body),
});

function rowOf(cells, active, onClick) {
  const tr = document.createElement('tr');
  tr.classList.toggle('active', active);
  for (const c of cells) {
    const td = tr.appendChild(document.createElement('td'));
    if (c instanceof Node) td.appendChild(c);
    else td.textContent = c;
  }
  tr.addEventListener('click', onClick);
  return tr;
}

// Switching tabs does not lose unsaved text, so no confirmation is needed
for (const tab of document.querySelectorAll('.tab')) {
  tab.addEventListener('click', () => {
    for (const t of document.querySelectorAll('.tab')) t.classList.toggle('active', t === tab);
    for (const p of document.querySelectorAll('.panel')) p.hidden = p.id !== `tab-${tab.dataset.tab}`;
    if (tab.dataset.tab === 'prompts') loadPrompts();
    if (tab.dataset.tab === 'bench') loadBench();
    if (tab.dataset.tab === 'tests') loadTests();
    if (tab.dataset.tab === 'eval') loadEval();
    if (tab.dataset.tab === 'usage') loadUsage();
  });
}

window.addEventListener('beforeunload', e => { if (docDirty() || promptDirty() || benchDirty()) e.preventDefault(); });

// ---- Documents ----

let docName = null;
let docSaved = '';

const docDirty = () => docName !== null && $('doc-text').value !== docSaved;
const docLeaveOk = () => !docDirty() || confirm(`"${docName}" chưa lưu. Bỏ thay đổi?`);
const docUrl = name => `${docsApi}/${encodeURIComponent(name)}`;
const putDoc = (name, content) => send(docUrl(name), 'PUT', { content });

function renderDocs(data) {
  $('doc-rows').replaceChildren(...data.files.map(f => {
    const tr = rowOf(
      [f.name, (f.bytes / 1024).toFixed(1), f.sections, fmtShort(f.modified)],
      f.name === docName,
      () => openDoc(f.name));
    tr.children[1].className = tr.children[2].className = 'num';
    return tr;
  }));
  $('doc-summary').textContent =
    `${data.files.length} file · ${data.totalChars.toLocaleString('vi-VN')} ký tự trong prompt · bản ${data.hash}`;
}

function showDoc(name, content, isNew) {
  docName = name;
  docSaved = isNew ? null : content;
  $('doc-empty').hidden = true;
  $('doc-edit').hidden = false;
  $('doc-name').textContent = name;
  $('doc-text').value = content;
  $('doc-download').href = `${docUrl(name)}?download=true`;
  $('doc-download').hidden = $('doc-delete').hidden = isNew;
  refreshDoc();
}

function closeDoc() {
  docName = null;
  $('doc-empty').hidden = false;
  $('doc-edit').hidden = true;
}

function refreshDoc() {
  $('doc-preview').innerHTML = DOMPurify.sanitize(marked.parse($('doc-text').value));
  $('doc-state').textContent = docDirty() ? 'chưa lưu' : '';
  $('doc-save').disabled = !docDirty();
}

async function openDoc(name) {
  if (name === docName || !docLeaveOk()) return;
  const doc = await call(docUrl(name));
  showDoc(doc.name, doc.content, false);
  renderDocs(await call(docsApi));
}

$('doc-text').addEventListener('input', refreshDoc);

$('doc-save').addEventListener('click', async () => {
  try {
    const content = $('doc-text').value;
    const data = await putDoc(docName, content);
    showDoc(docName, content, false);
    renderDocs(data);
  } catch (e) { alert(e.message); }
});

$('doc-delete').addEventListener('click', async () => {
  if (!confirm(`Xóa hẳn "${docName}"? Không khôi phục được.`)) return;
  try {
    const data = await call(docUrl(docName), { method: 'DELETE' });
    closeDoc();
    renderDocs(data);
  } catch (e) { alert(e.message); }
});

$('doc-new').addEventListener('click', async () => {
  if (!docLeaveOk()) return;
  let name = prompt('Tên file mới (đuôi .md):');
  if (!name) return;
  name = name.trim();
  if (!name.endsWith('.md')) name += '.md';
  const list = await call(docsApi);
  if (list.files.some(f => f.name === name)) return alert(`Đã có file "${name}".`);
  showDoc(name, `# ${name.replace(/\.md$/, '')}\n\n`, true);
  renderDocs(list);
});

$('doc-upload').addEventListener('click', () => $('doc-file').click());

$('doc-file').addEventListener('change', async e => {
  if (!docLeaveOk()) return (e.target.value = '');
  const existing = new Set((await call(docsApi)).files.map(f => f.name));
  let data = null;
  for (const file of e.target.files) {
    if (existing.has(file.name) && !confirm(`Đã có "${file.name}". Thay bằng file mới?`)) continue;
    try { data = await putDoc(file.name, await file.text()); }
    catch (err) { alert(`${file.name}: ${err.message}`); }
  }
  e.target.value = '';
  if (data) {
    closeDoc();
    renderDocs(data);
  }
});

// ---- Prompt ----

let prompts = [];
let promptId = null;
let promptDraft = null;

// promptDraft: null when viewing; { mode: 'new' | 'edit', base, note } while editing
const promptDirty = () => promptDraft !== null &&
  ($('prompt-text').value !== promptDraft.base || $('prompt-note').value !== promptDraft.note);
const promptLeaveOk = () => !promptDirty() || confirm('Prompt đang soạn chưa lưu. Bỏ thay đổi?');

function renderPrompts() {
  $('prompt-rows').replaceChildren(...prompts.map(p => {
    const item = el('div', p.id === promptId ? 'version active' : 'version');
    const top = item.appendChild(el('div', 'top'));
    top.append(el('b', null, `Bản ${p.id}`));
    if (p.isActive) top.append(el('span', 'badge', 'đang dùng'));
    item.append(el('div', 'note-text', p.note ?? '(không ghi chú)'), el('div', 'note', fmtTime(p.createdAt)), scoreLine(p.scores));
    item.addEventListener('click', () => showPrompt(p.id));
    return item;
  }));
}

function showPrompt(id) {
  if (!promptLeaveOk()) return;
  const p = prompts.find(x => x.id === id);
  setChatEnabled(true);
  if (id !== promptId) resetChat(`Chat thử với bản ${id}`);
  promptId = id;
  promptDraft = null;
  $('prompt-empty').hidden = true;
  $('prompt-view').hidden = false;
  $('prompt-view-bar').hidden = false;
  $('prompt-new-bar').hidden = true;
  $('prompt-title').textContent = `Bản ${p.id}` + (p.note ? ` · ${p.note}` : '');
  $('prompt-badge').hidden = !p.isActive;
  $('prompt-activate').hidden = !!p.isActive;
  $('prompt-text').readOnly = true;
  $('prompt-text').value = p.content;
  renderPrompts();
}

async function loadPrompts() {
  try {
    prompts = await call(promptsApi);
    if (promptId === null) showPrompt(prompts.find(p => p.isActive).id);
    else renderPrompts();
  } catch (e) { alert(e.message); }
}

function startDraft(mode) {
  const p = prompts.find(x => x.id === promptId);
  promptDraft = { mode, base: p.content, note: mode === 'edit' ? (p.note ?? '') : '' };
  $('prompt-view-bar').hidden = true;
  $('prompt-new-bar').hidden = false;
  $('prompt-new-title').textContent = mode === 'edit' ? `Sửa bản ${p.id}` : `Bản mới (từ bản ${p.id})`;
  $('prompt-save').textContent = mode === 'edit' ? 'Lưu' : 'Lưu bản mới';
  $('prompt-note').value = promptDraft.note;
  $('prompt-save').disabled = true;
  setChatEnabled(false);
  $('prompt-text').readOnly = false;
  $('prompt-text').focus();
}

$('prompt-edit').addEventListener('click', () => startDraft('edit'));
$('prompt-fork').addEventListener('click', () => startDraft('new'));

for (const id of ['prompt-text', 'prompt-note']) {
  $(id).addEventListener('input', () => { $('prompt-save').disabled = !promptDirty(); });
}

$('prompt-cancel').addEventListener('click', () => showPrompt(promptId));

$('prompt-save').addEventListener('click', async () => {
  const body = { content: $('prompt-text').value, note: $('prompt-note').value };
  const editing = promptDraft.mode === 'edit';
  if (editing && prompts.find(x => x.id === promptId).isActive &&
      !confirm(`Bản ${promptId} đang dùng. Lưu là nhân viên nhận nội dung mới từ câu hỏi tiếp theo.`)) return;
  try {
    const { id } = editing
      ? await send(`${promptsApi}/${promptId}`, 'PUT', body)
      : await send(promptsApi, 'POST', body);
    promptDraft = null;
    prompts = await call(promptsApi);
    showPrompt(id);
  } catch (e) { alert(e.message); }
});

$('prompt-activate').addEventListener('click', async () => {
  if (!confirm(`Kích hoạt bản ${promptId}? Nhân viên dùng prompt này từ câu hỏi tiếp theo.`)) return;
  try {
    await call(`${promptsApi}/${promptId}/activate`, { method: 'POST' });
    prompts = await call(promptsApi);
    showPrompt(promptId);
  } catch (e) { alert(e.message); }
});

// ---- Test chat ----

const chatUser = 'admin-thử';
let chatSession = null;
let chatBusy = false;
let chatOff = false;
// Bumped on every reset so a still-streaming answer from the old chat is not written into the new one
let chatGen = 0;
const chatAttach = attachImages({
  form: $('chat-form'), input: $('chat-input'), button: $('chat-attach'), list: $('chat-attachments'), error: $('chat-attach-error'),
});

const md = text => DOMPurify.sanitize(marked.parse(text));

function chatEmpty(text) {
  const p = document.createElement('p');
  p.className = 'empty';
  p.textContent = text;
  $('chat-messages').replaceChildren(p);
}

function resetChat(title) {
  chatGen++;
  chatSession = null;
  chatBusy = false;
  chatAttach.clear();
  if (title) $('chat-title').textContent = title;
  chatEmpty('Câu trả lời dùng đúng bản prompt đang xem, kể cả khi bản đó chưa kích hoạt.');
  $('chat-input').disabled = $('chat-new').disabled = $('chat-send').disabled = $('chat-attach').disabled = chatOff;
}

// A new draft has no id to chat with yet; leaving edit mode starts a new chat
function setChatEnabled(on) {
  if (on === !chatOff) return;
  chatOff = !on;
  resetChat();
  if (!on) chatEmpty('Lưu bản mới rồi mới chat thử được.');
}

function chatAdd(cls, text, images) {
  $('chat-messages').querySelector('.empty')?.remove();
  const div = $('chat-messages').appendChild(document.createElement('div'));
  div.className = `msg ${cls}`;
  if (images?.length) div.append(imageStrip(images));
  if (text) div.append(text);
  return div;
}

async function chatSend(text, images) {
  const gen = chatGen;
  chatBusy = true;
  $('chat-send').disabled = true;
  chatAdd('user', text, images);
  const bot = chatAdd('bot pending', '');
  let answer = '';
  try {
    const res = await fetch('/api/chat/stream', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ sessionId: chatSession, userName: chatUser, message: text, images, promptId }),
    });
    if (!res.ok) throw new Error(await res.text());
    for await (const { event, data } of readSse(res)) {
      if (gen !== chatGen) return;
      if (event === 'delta') {
        answer += data.text;
        bot.innerHTML = md(answer);
        bot.scrollIntoView({ block: 'end' });
      } else if (event === 'done') {
        chatSession = data.sessionId;
        const meta = document.createElement('p');
        meta.className = 'meta';
        meta.textContent = `${(data.latencyMs / 1000).toFixed(1)} s · ${data.inputTokens ?? '?'} vào / ${data.outputTokens ?? '?'} ra`;
        bot.after(meta);
      } else if (event === 'error') {
        throw new Error(data.message);
      }
    }
  } catch (e) {
    if (gen !== chatGen) return;
    bot.classList.add('error');
    bot.textContent = `Lỗi: ${e.message}`;
  } finally {
    if (gen === chatGen) {
      bot.classList.remove('pending');
      chatBusy = false;
      $('chat-send').disabled = chatOff;
    }
  }
}

$('chat-form').addEventListener('submit', e => {
  e.preventDefault();
  const text = $('chat-input').value.trim();
  const images = chatAttach.images();
  if ((!text && !images.length) || chatBusy || promptDraft || chatAttach.busy()) return;
  $('chat-input').value = '';
  chatAttach.clear();
  chatSend(text, images);
});

$('chat-input').addEventListener('keydown', e => {
  if (e.key === 'Enter' && !e.shiftKey && !e.isComposing) {
    e.preventDefault();
    $('chat-form').requestSubmit();
  }
});

$('chat-new').addEventListener('click', () => resetChat());

// ---- Benchmark set ----

const benchApi = '/api/admin/bench';
const benchFields = ['id', 'kind-input', 'question', 'expected', 'sources', 'nhanh', 'trung'];
let questions = [];
let benchId = null;
// Form values when opened, to detect unsaved changes; null when no question is open
let benchSaved = null;

const benchForm = () => benchFields.map(f => {
  const e = $(`bench-${f}`);
  return e.type === 'checkbox' ? e.checked : e.value;
}).join('\u0000');
const benchDirty = () => benchSaved !== null && benchForm() !== benchSaved;
const benchLeaveOk = () => !benchDirty() || confirm('Câu đang sửa chưa lưu. Bỏ thay đổi?');
const tierMark = q => (q.nhanh ? '⚡' : '') + (q.trung ? '★' : '');

function renderBench() {
  const term = $('bench-search').value.trim().toLowerCase();
  const kind = $('bench-kind').value;
  const tier = $('bench-tier').value;
  const shown = questions.filter(q =>
    (!kind || q.kind === kind) &&
    (!tier || q[tier]) &&
    (!term || [q.id, q.question, q.expected, q.sources].some(v => v.toLowerCase().includes(term))));
  $('bench-rows').replaceChildren(...shown.map(q => {
    const text = el('div', 'clip', q.question);
    text.title = q.question;
    return rowOf([q.id, tierMark(q), q.kind, text], q.id === benchId, () => openQuestion(q.id));
  }));
  const n = t => questions.filter(q => q[t]).length;
  $('bench-summary').textContent = shown.length === questions.length
    ? `${questions.length} câu · ⚡${n('nhanh')} · ★${n('trung')}`
    : `${shown.length}/${questions.length} câu`;
}

async function loadBench() {
  try {
    questions = await call(benchApi);
    const kinds = [...new Set(questions.map(q => q.kind))].sort((a, b) => a.localeCompare(b, 'vi'));
    const current = $('bench-kind').value;
    $('bench-kind').replaceChildren(el('option', null, 'Mọi loại'), ...kinds.map(k => el('option', null, k)));
    $('bench-kind').firstChild.value = '';
    $('bench-kind').value = kinds.includes(current) ? current : '';
    $('bench-kinds').replaceChildren(...kinds.map(k => { const o = el('option'); o.value = k; return o; }));
    renderBench();
  } catch (e) { alert(e.message); }
}

function showQuestion(q, isNew) {
  benchId = isNew ? null : q.id;
  $('bench-empty').hidden = true;
  $('bench-form').hidden = false;
  $('bench-title').textContent = isNew ? 'Câu mới' : q.id;
  $('bench-id').value = q.id;
  $('bench-id').readOnly = !isNew;
  $('bench-kind-input').value = q.kind;
  $('bench-question').value = q.question;
  $('bench-expected').value = q.expected;
  $('bench-sources').value = q.sources;
  $('bench-nhanh').checked = q.nhanh;
  $('bench-trung').checked = q.trung;
  $('bench-delete').hidden = isNew;
  benchSaved = isNew ? '' : benchForm();
  refreshQuestion();
  renderBench();
  (isNew ? $('bench-id') : $('bench-question')).focus();
}

function refreshQuestion() {
  if ($('bench-nhanh').checked) $('bench-trung').checked = true;
  $('bench-trung').disabled = $('bench-nhanh').checked;
  $('bench-state').textContent = benchDirty() ? 'chưa lưu' : '';
  $('bench-save').disabled = !benchDirty();
}

function openQuestion(id) {
  if (id === benchId || !benchLeaveOk()) return;
  showQuestion(questions.find(q => q.id === id), false);
}

$('bench-form').addEventListener('input', refreshQuestion);
for (const id of ['bench-search', 'bench-kind', 'bench-tier']) $(id).addEventListener('input', renderBench);

// Suggest the next id for the current question's prefix, e.g. on LV-07 suggest LV-21 if LV-20 is the highest
function nextId() {
  const prefix = benchId?.split('-')[0];
  if (!prefix) return '';
  const nums = questions.filter(q => q.id.startsWith(prefix + '-')).map(q => parseInt(q.id.slice(prefix.length + 1), 10));
  return `${prefix}-${String(Math.max(0, ...nums) + 1).padStart(2, '0')}`;
}

$('bench-new').addEventListener('click', () => {
  if (!benchLeaveOk()) return;
  const base = questions.find(q => q.id === benchId);
  showQuestion({ id: nextId(), kind: base?.kind ?? '', question: '', expected: '', sources: '', nhanh: false, trung: false }, true);
});

$('bench-form').addEventListener('submit', async e => {
  e.preventDefault();
  const body = {
    id: $('bench-id').value.trim(),
    kind: $('bench-kind-input').value,
    question: $('bench-question').value,
    expected: $('bench-expected').value,
    sources: $('bench-sources').value,
    nhanh: $('bench-nhanh').checked,
    trung: $('bench-trung').checked,
  };
  try {
    const saved = benchId === null
      ? await send(benchApi, 'POST', body)
      : await send(`${benchApi}/${encodeURIComponent(benchId)}`, 'PUT', body);
    tierCounts = null;
    questions = await call(benchApi);
    showQuestion(saved, false);
  } catch (err) { alert(err.message); }
});

$('bench-delete').addEventListener('click', async () => {
  if (!confirm(`Xóa câu ${benchId}? Kết quả các lượt test cũ vẫn giữ.`)) return;
  try {
    await call(`${benchApi}/${encodeURIComponent(benchId)}`, { method: 'DELETE' });
    tierCounts = null;
    benchId = null;
    benchSaved = null;
    $('bench-form').hidden = true;
    $('bench-empty').hidden = false;
    await loadBench();
  } catch (err) { alert(err.message); }
});

// ---- Test runs ----

const testsApi = '/api/admin/tests';
const tierNames = { nhanh: 'Nhanh', trung: 'Trung', full: 'Full' };
const gradeClass = { 'Đúng': 'g-dung', 'Đúng một phần': 'g-motphan', 'Sai': 'g-sai', 'Bịa': 'g-bia' };
let tierCounts = null;
let testTier = 'nhanh';
let runs = [];
let runId = null;
let pollGen = 0;
let testFilter = 'all';
let detailQid = null;
let detailTab = 'score';
// Results of the previous run on the same set, for per-question comparison
const runCache = new Map();

const fmtShort = s => new Date(s).toLocaleString('vi-VN', { hour: '2-digit', minute: '2-digit', day: '2-digit', month: '2-digit' });
const fmtScore = s => s == null ? '—' : Number(s.toFixed(1)).toLocaleString('vi-VN');
const fmtNum = n => n.toLocaleString('vi-VN');
const fmtTok = n => n >= 1e6 ? `${(n / 1e6).toLocaleString('vi-VN', { maximumFractionDigits: 2 })} tr`
  : n >= 1e3 ? `${Math.round(n / 1e3)}k` : String(n);

function el(tag, cls, text) {
  const e = document.createElement(tag);
  if (cls) e.className = cls;
  if (text != null) e.textContent = text;
  return e;
}

function staleMark(run) {
  const e = el('span', 'stale', run.stale ? ' (cũ)' : '');
  if (run.stale) e.title = `Điểm đo trước khi ${run.stale}`;
  return e;
}

// "Nhanh 100 · Trung — · Full —" under each prompt version
function scoreLine(scores) {
  const line = el('div', 'scores');
  Object.keys(tierNames).forEach((t, i) => {
    const r = scores?.[t];
    if (i) line.append(' · ');
    line.append(`${tierNames[t]} ${r ? fmtScore(r.score) : '—'}`);
    if (r?.bia) line.append(` (${r.bia} Bịa)`);
    if (r) line.appendChild(staleMark(r));
  });
  return line;
}

const criteriaScale = 'criteria8';
const parseCriteria = r => r.criteriaJson ? JSON.parse(r.criteriaJson) : null;
const failsOf = r => (parseCriteria(r) ?? []).filter(c => c.pass === false).map(c => c.criterion);

const criteriaNames = {
  1: 'Hiểu đúng ý người hỏi', 2: 'Dùng đúng ngữ cảnh lượt trước', 3: 'Bám tài liệu', 4: 'Đủ ý',
  5: 'Từ chối đúng', 6: 'Trích nguồn đúng mục', 7: 'Gọn, không lặp', 8: 'Tuân thủ',
};
const criteriaShort = { 1: 'Ý', 2: 'NC', 3: 'TL', 4: 'Đủ', 5: 'TC', 6: 'Ng', 7: 'Gọn', 8: 'TT' };

function passCell(pass) {
  if (pass === true) return el('i', 'cell p', '✓');
  if (pass === false) return el('i', 'cell f', '✗');
  return el('i', 'cell n', '–');
}

// Latest finished run before this one, with the same set and scale
const prevRun = run => runs
  .filter(x => x.id < run.id && x.tier === run.tier && x.scale === run.scale && x.status === 'done')
  .sort((a, b) => b.id - a.id)[0];

function delta(cur, prev, higherBetter, fmt = v => fmtScore(Math.abs(v))) {
  if (cur == null || prev == null || cur === prev) return null;
  const d = cur - prev;
  return el('small', (d > 0) === higherBetter ? 'up' : 'down', `${d > 0 ? '+' : '−'}${fmt(d)}`);
}

function renderTiers() {
  $('test-tiers').replaceChildren(...Object.keys(tierNames).map(t => {
    const b = el('button', t === testTier ? 'on' : null, tierNames[t]);
    b.type = 'button';
    b.appendChild(el('small', null, tierCounts[t]));
    b.addEventListener('click', () => { testTier = t; renderTiers(); });
    return b;
  }));
}

async function loadTests() {
  try {
    if (!tierCounts) {
      tierCounts = await call(`${testsApi}/tiers`);
      renderTiers();
    }
    prompts = await call(promptsApi);
    const selected = Number($('test-prompt').value) || promptId || prompts.find(p => p.isActive).id;
    $('test-prompt').replaceChildren(...prompts.map(p => {
      const o = el('option', null, `Bản ${p.id}${p.isActive ? ' · đang dùng' : ''}${p.note ? ` · ${p.note}` : ''}`);
      o.value = p.id;
      return o;
    }));
    $('test-prompt').value = selected;
    await loadRuns();
  } catch (e) { alert(e.message); }
}

// "#12 · Bản 1 · Nhanh · 100 (+7) · 15:37 04/10"
function runLabel(r) {
  let score = { running: `đang chạy ${r.done}/${r.total}`, cancelled: 'đã hủy', failed: 'lỗi' }[r.status];
  if (!score) {
    const prev = prevRun(r);
    const d = prev && r.score != null && prev.score != null ? r.score - prev.score : 0;
    score = `${fmtScore(r.score)} điểm${d ? ` (${d > 0 ? '+' : '−'}${fmtScore(Math.abs(d))})` : ''}${r.scale !== criteriaScale ? ' · thang cũ' : r.stale ? ' · cũ' : ''}`;
  }
  return `#${r.id} · Bản ${r.promptId} · ${tierNames[r.tier]} · ${score} · ${fmtShort(r.startedAt)}`;
}

async function loadRuns() {
  runs = await call(testsApi);
  $('run-select').replaceChildren(...runs.map(r => {
    const o = el('option', null, runLabel(r));
    o.value = r.id;
    return o;
  }));
  if (runId !== null) $('run-select').value = runId;
  $('run-select').hidden = !runs.length;
  if (!runs.length) { $('test-empty').hidden = false; $('test-view').hidden = true; }
  const running = runs.find(r => r.status === 'running');
  $('test-start').disabled = !!running;
  $('test-start').title = running ? `Đang chạy lượt #${running.id}` : '';
  if (runId === null && runs.length) openRun((running ?? runs[0]).id);
}

function openRun(id) {
  runId = id;
  testFilter = 'all';
  detailQid = null;
  refreshRun(++pollGen);
}

// Poll a running run every 2 seconds so per-question results appear progressively
async function refreshRun(gen) {
  let data;
  try { data = await call(`${testsApi}/${runId}`); }
  catch (e) { return alert(e.message); }
  if (gen !== pollGen) return;
  renderRun(data);
  if (data.run.status === 'running') setTimeout(() => refreshRun(gen), 2000);
  await loadRuns();
}

// A just-finished run has no plan on the server; keep the order so rows do not reshuffle
const planCache = new Map();

function renderKpis(run, prev) {
  const kpi = (label, value, sub, cls) => {
    const k = el('div', cls ? `kpi ${cls}` : 'kpi');
    k.append(el('span', null, label), el('b', null, value));
    if (sub) k.append(sub instanceof Node ? sub : el('small', null, sub));
    return k;
  };
  const vs = (cur, old, higherBetter, unit = '') => {
    if (!prev) return null;
    const d = delta(cur, old, higherBetter, v => `${fmtNum(Math.abs(Math.round(v * 10) / 10))}${unit}`);
    if (!d) return el('small', null, `bằng #${prev.id}`);
    d.append(` so với #${prev.id}`);
    return d;
  };
  const items = [kpi('Điểm trung bình', fmtScore(run.score), vs(run.score, prev?.score, true) ?? (run.status === 'running' ? 'tạm tính' : 'lượt đầu của bộ này'), 'hero')];
  if (run.scale === criteriaScale) {
    items.push(
      kpi('Đạt hết 8 tiêu chí', fmtNum(run.perfect), vs(run.perfect, prev?.perfect, true, ' câu') ?? `/ ${run.done} câu`),
      kpi('Có rớt', fmtNum(run.failed), vs(run.failed, prev?.failed, false, ' câu') ?? 'câu'),
      kpi('Lỗi gọi model', fmtNum(run.loi), vs(run.loi, prev?.loi, false, ' câu') ?? 'câu'));
  } else {
    for (const [label, n] of [['Đúng', run.dung], ['Một phần', run.motPhan], ['Sai', run.sai], ['Bịa', run.bia], ['Lỗi', run.loi]]) {
      items.push(kpi(label, fmtNum(n), 'câu'));
    }
  }
  if (run.avgLatencyMs != null) items.push(kpi('Mỗi câu', `${(run.avgLatencyMs / 1000).toLocaleString('vi-VN', { maximumFractionDigits: 1 })} s`, 'trung bình'));
  if (run.inputTokens != null) items.push(kpi('Token vào', fmtTok(run.inputTokens), 'của bot, chưa tính judge'));
  $('test-kpis').replaceChildren(...items);
}

function renderRun(data) {
  const { run, results, active } = data;
  if (data.plan) planCache.set(run.id, data.plan);
  const plan = planCache.get(run.id);
  const crit = run.scale === criteriaScale;
  $('test-empty').hidden = true;
  $('test-view').hidden = false;
  $('test-sub').textContent = `Lượt #${run.id} · Bản ${run.promptId} · bộ ${tierNames[run.tier]}, ${run.total} câu · ${run.model}`;
  $('test-running').hidden = run.status !== 'running';
  $('test-status').textContent = {
    running: `Đang chạy ${run.done}/${run.total} câu`,
    done: `Xong lúc ${fmtTime(run.finishedAt)}`,
    cancelled: `Đã hủy ở câu ${run.done}/${run.total}`,
    failed: `Lỗi: ${run.error ?? ''}`,
  }[run.status];
  $('test-cancel').hidden = run.status !== 'running';

  const prev = prevRun(run);
  if (prev && !runCache.has(prev.id)) {
    runCache.set(prev.id, null);
    call(`${testsApi}/${prev.id}`).then(d => {
      runCache.set(prev.id, d);
      if (runId === run.id) renderRun(data);
    }).catch(() => runCache.delete(prev.id));
  }
  const prevBy = runCache.get(prev?.id) ? new Map(runCache.get(prev.id).results.map(r => [r.qid, r])) : null;
  renderKpis(run, prev);

  // While running, render from the plan (all questions, file order); afterwards only DB results remain
  const byQid = new Map(results.map(r => [r.qid, r]));
  const busy = new Set(active ?? []);
  const items = plan ? plan.map(q => byQid.get(q.qid) ?? q) : results;
  const failed = r => crit ? failsOf(r).length > 0 : r.grade != null && r.grade !== 'Đúng';
  const changed = r => {
    const p = prevBy?.get(r.qid);
    return !!p && (crit ? p.score !== r.score || failsOf(p).join() !== failsOf(r).join() : p.grade !== r.grade);
  };
  const filters = [
    ['all', 'Tất cả', () => true],
    ['fail', 'Có rớt', r => r && failed(r)],
    ['err', 'Lỗi', r => r && r.error != null],
  ];
  if (prevBy) filters.push(['changed', `Khác #${prev.id}`, r => r && changed(r)]);
  if (!filters.some(f => f[0] === testFilter)) testFilter = 'all';
  $('test-filters').replaceChildren(...filters.map(([key, label, test]) => {
    const n = items.filter(i => test(byQid.get(i.qid))).length;
    const b = el('button', key === testFilter ? 'on' : null, `${label} ${n}`);
    b.type = 'button';
    b.addEventListener('click', () => { testFilter = key; renderRun(data); });
    return b;
  }));
  const test = filters.find(f => f[0] === testFilter)[2];

  $('matrix-title').textContent = crit ? 'Ma trận 8 tiêu chí' : 'Kết quả (thang 4 mức cũ)';
  $('matrix-legend').hidden = !crit;
  const head = el('tr');
  head.append(el('th', null, 'Mã'), el('th', null, 'Câu hỏi'));
  if (crit) {
    for (const k of Object.keys(criteriaNames)) {
      const th = head.appendChild(el('th', 'c'));
      th.title = `${k}. ${criteriaNames[k]}`;
      th.append(el('b', null, k), criteriaShort[k]);
    }
  } else {
    head.append(el('th', null, 'Kết quả'), el('th', null, 'Trích nguồn'));
  }
  head.append(el('th', 'num', 'Điểm'));
  $('matrix-head').replaceChildren(head);

  const rows = [];
  for (const item of items) {
    const r = byQid.get(item.qid);
    if (!test(r)) continue;
    const state = r ? 'done' : busy.has(item.qid) ? 'active' : 'pending';
    const tr = el('tr', [r ? null : 'pending', item.qid === detailQid ? 'active' : null].filter(Boolean).join(' '));
    const id = tr.appendChild(el('td', 'id', item.qid));
    if (state === 'active') id.append(el('span', 'spin', ''));
    const q = tr.appendChild(el('td', 'q', item.question));
    q.title = item.question;
    if (crit) {
      const byK = new Map((r ? parseCriteria(r) ?? [] : []).map(c => [c.criterion, c.pass]));
      for (const k of Object.keys(criteriaNames)) {
        const td = tr.appendChild(el('td', 'c'));
        td.append(r && byK.size ? passCell(byK.get(Number(k))) : el('i', 'cell w', ''));
      }
    } else {
      tr.appendChild(el('td')).append(r ? el('span', `grade ${gradeClass[r.grade] ?? 'g-loi'}`, r.grade ?? 'Lỗi') : '');
      tr.append(el('td', null, r?.citeCheck ?? ''));
    }
    tr.append(el('td', 's', r ? (r.error ? 'lỗi' : fmtScore(r.score)) : state === 'active' ? '…' : ''));
    if (r) tr.addEventListener('click', () => { detailQid = r.qid; detailTab = 'score'; renderRun(data); });
    rows.push(tr);
  }
  if (crit && results.length) {
    const counts = Object.keys(criteriaNames).map(k => results.filter(r => failsOf(r).includes(Number(k))).length);
    const max = Math.max(...counts);
    const sum = el('tr', 'sum');
    sum.append(el('td', 'id', 'Σ'), el('td', null, 'Số câu rớt'));
    for (const n of counts) sum.appendChild(el('td', 'c')).append(el('span', n === 0 ? 'fc' : n === max ? 'fc max' : 'fc some', n));
    sum.append(el('td'));
    rows.push(sum);
  }
  if (!rows.length) rows.push(Object.assign(el('tr', 'sum'), { innerHTML: '<td colspan="12" class="note">Không có câu nào.</td>' }));
  $('result-rows').replaceChildren(...rows);

  const r = byQid.get(detailQid);
  if (r) renderDetail(r, prevBy?.get(r.qid), prev, crit);
  else $('test-detail').replaceChildren(el('p', 'empty', 'Bấm một câu trong bảng để xem chi tiết.'));
}

const citedText = answer => [...(answer ?? '').matchAll(/\(Nguồn:\s*([^)]*)\)/g)].map(m => m[1]).join('; ') || '—';

function renderDetail(r, p, prev, crit) {
  const box = $('test-detail');
  const dh = el('div', 'dh');
  const row = dh.appendChild(el('div', 'row'));
  row.append(el('code', null, r.qid), el('span', 'kind', r.kind), el('b', 'score', r.error ? 'lỗi' : fmtScore(r.score)));
  dh.append(el('p', 'q', r.question));

  const tabs = el('div', 'dtabs');
  for (const [key, label] of [['score', 'Chấm điểm'], ['answer', 'Đáp án và bot']]) {
    const b = tabs.appendChild(el('button', key === detailTab ? 'on' : null, label));
    b.type = 'button';
    b.addEventListener('click', () => { detailTab = key; renderDetail(r, p, prev, crit); });
  }

  const db = el('div', 'db');
  if (detailTab === 'score') {
    if (r.error) db.append(el('div', 'prev', r.error));
    const criteria = parseCriteria(r);
    if (criteria) {
      const crits = db.appendChild(el('div', 'crits'));
      const bad = criteria.filter(c => c.pass === false);
      for (const c of bad) {
        const cr = crits.appendChild(el('div', 'cr'));
        cr.append(passCell(false), el('b', null, `${c.criterion} · ${criteriaNames[c.criterion]}`), el('p', null, c.reason));
      }
      if (!bad.length) crits.append(el('div', 'note', 'Đạt mọi tiêu chí áp dụng.'));
      const ok = criteria.filter(c => c.pass === true).map(c => c.criterion);
      const na = criteria.filter(c => c.pass == null).map(c => c.criterion);
      db.append(el('div', 'okline', [ok.length && `Đạt: ${ok.join(', ')}`, na.length && `Không áp dụng: ${na.join(', ')}`].filter(Boolean).join(' · ')));
    } else if (r.grade) {
      const g = db.appendChild(el('div'));
      g.append(el('span', `grade ${gradeClass[r.grade] ?? 'g-loi'}`, r.grade));
      if (r.gradeReason) db.append(el('p', 'note', r.gradeReason));
    }
    const cmp = db.appendChild(el('div', 'cmp'));
    cmp.append(el('em', null, 'Nguồn chuẩn'), el('span', null, r.sources || '—'),
      el('em', null, 'Bot trích'), el('span', null, citedText(r.answer)),
      el('em', null, 'So khớp'), el('span', null, r.citeCheck ?? '—'));
    if (p) {
      const fails = crit ? failsOf(p) : [];
      const text = `Lượt #${prev.id}: ${p.error ? 'lỗi' : fmtScore(p.score)} điểm${fails.length ? ` (rớt ${fails.join(', ')})` : crit && !p.error ? ' (đạt hết)' : ''}`;
      db.append(el('div', r.score != null && p.score != null && r.score >= p.score ? 'prev better' : 'prev', text));
    }
  } else {
    db.append(el('h4', null, 'Đáp án chuẩn'));
    const exp = db.appendChild(el('div', 'answer-box prose small'));
    exp.innerHTML = md(r.expected);
    db.append(el('h4', null, 'Câu trả lời của bot'));
    const ans = db.appendChild(el('div', 'prose small'));
    ans.innerHTML = md(r.answer ?? r.error ?? '');
    if (r.latencyMs != null) db.append(el('div', 'note', `${(r.latencyMs / 1000).toFixed(1)} s · ${fmtNum(r.inputTokens)} vào / ${fmtNum(r.outputTokens)} ra`));
    if (r.gradeReason && parseCriteria(r)) {
      db.append(el('h4', null, 'Nhận xét judge'));
      db.appendChild(el('div', 'prose small')).innerHTML = md(r.gradeReason);
    }
  }
  box.replaceChildren(dh, tabs, db);
}

async function startTest() {
  const pid = Number($('test-prompt').value);
  if (!confirm(`Chạy bộ ${tierNames[testTier]} (${tierCounts[testTier]} câu) với bản ${pid}? Mỗi câu tốn khoảng 100k token (bot + judge).`)) return;
  try {
    const { runId: id } = await send(testsApi, 'POST', { promptId: pid, tier: testTier });
    openRun(id);
  } catch (e) { alert(e.message); }
}

$('test-start').addEventListener('click', startTest);
$('run-select').addEventListener('change', () => openRun(Number($('run-select').value)));

$('test-cancel').addEventListener('click', async () => {
  if (!confirm(`Hủy lượt #${runId}? Các câu đã chấm vẫn được giữ.`)) return;
  try { await call(`${testsApi}/${runId}/cancel`, { method: 'POST' }); }
  catch (e) { alert(e.message); }
});

// 7/30/90-day button group used on the Eval and Token tabs
function segValue(id, onChange) {
  for (const b of $(id).querySelectorAll('button')) {
    b.addEventListener('click', () => {
      for (const x of $(id).querySelectorAll('button')) x.classList.toggle('on', x === b);
      onChange();
    });
  }
  return () => Number($(id).querySelector('.on').dataset.days);
}

// ---- Real chat eval ----

const evalApi = '/api/admin/evals';
let evalId = null;
const evalDays = segValue('eval-days', () => loadEval());

async function loadEval() {
  const days = evalDays();
  try {
    const [daily, failed] = await Promise.all([call(`${evalApi}/daily?days=${days}`), call(`${evalApi}/failed?days=${days}`)]);
    renderChart(daily, days);
    const total = daily.reduce((n, d) => n + d.sessions, 0);
    $('eval-summary').textContent = total
      ? `${total} session đã chấm, ${failed.length} session bị rớt trong ${days} ngày`
      : `Chưa có session nào được chấm trong ${days} ngày`;
    $('eval-rows').replaceChildren(...(failed.length ? failed.map(f => {
      const tr = rowOf(
        [fmtShort(f.sessionAt), f.userName, f.firstQuestion, fmtScore(f.score), f.failed.map(c => c.criterion).join(', ')],
        f.evalId === evalId,
        () => openEval(f.evalId));
      tr.children[2].className = 'clip';
      tr.children[2].title = f.firstQuestion;
      tr.children[3].className = 'num';
      tr.children[4].title = f.failed.map(c => `${c.criterion}. ${c.name}`).join('\n');
      return tr;
    }) : [Object.assign(el('tr', 'static'), { innerHTML: '<td colspan="5" class="note">Không có session nào bị rớt.</td>' })]));
  } catch (e) { alert(e.message); }
}

// Hand-drawn SVG to avoid a chart library; days without sessions are gaps, not joined
function renderChart(daily, days) {
  const W = 420, H = 170, L = 28, R = 8, T = 10, B = 22;
  const ns = 'http://www.w3.org/2000/svg';
  const svg = document.createElementNS(ns, 'svg');
  svg.setAttribute('viewBox', `0 0 ${W} ${H}`);
  const add = (tag, attrs, text) => {
    const e = svg.appendChild(document.createElementNS(ns, tag));
    for (const [k, v] of Object.entries(attrs)) e.setAttribute(k, v);
    if (text != null) e.textContent = text;
    return e;
  };

  const start = new Date();
  start.setHours(0, 0, 0, 0);
  start.setDate(start.getDate() - (days - 1));
  const dayIndex = s => Math.round((new Date(`${s}T00:00:00`) - start) / 86400000);
  const x = i => L + (days === 1 ? 0 : i * (W - L - R) / (days - 1));
  const y = v => T + (100 - v) * (H - T - B) / 100;

  for (const v of [0, 50, 100]) {
    add('line', { x1: L, x2: W - R, y1: y(v), y2: y(v), class: 'grid' });
    add('text', { x: L - 6, y: y(v) + 4, class: 'axis', 'text-anchor': 'end' }, v);
  }
  const label = i => { const d = new Date(start); d.setDate(d.getDate() + i); return `${d.getDate()}/${d.getMonth() + 1}`; };
  for (const i of [0, Math.floor((days - 1) / 2), days - 1]) {
    add('text', { x: x(i), y: H - 6, class: 'axis', 'text-anchor': i === 0 ? 'start' : i === days - 1 ? 'end' : 'middle' }, label(i));
  }

  const points = daily.map(d => ({ ...d, i: dayIndex(d.date) })).filter(p => p.i >= 0 && p.i < days);
  let path = '';
  points.forEach((p, k) => { path += `${k && p.i === points[k - 1].i + 1 ? 'L' : 'M'}${x(p.i)},${y(p.avgScore)} `; });
  if (path) add('path', { d: path, class: 'line' });
  for (const p of points) {
    const c = add('circle', { cx: x(p.i), cy: y(p.avgScore), r: 3.5, class: 'dot' });
    c.appendChild(document.createElementNS(ns, 'title')).textContent = `${p.date}: ${fmtScore(p.avgScore)} điểm (${p.sessions} session)`;
  }
  $('eval-chart').replaceChildren(svg);
}

async function openEval(id) {
  evalId = id;
  let data;
  try { data = await call(`${evalApi}/${id}`); }
  catch (e) { return alert(e.message); }
  const { eval: ev, scores, turns } = data;
  $('eval-empty').hidden = true;
  $('eval-view').hidden = false;
  $('eval-score').textContent = fmtScore(ev.score);
  $('eval-title').textContent = `${ev.userName} · ${turns.length} lượt`;
  $('eval-meta').textContent =
    `Chat lúc ${fmtTime(ev.sessionAt)} · chấm lúc ${fmtTime(ev.createdAt)} (${ev.source === 'auto' ? 'tự động' : 'tay'}) · tài liệu ${ev.docHash}`;
  $('eval-scores').replaceChildren(...scores.map(s => {
    const cr = el('div', 'cr');
    cr.append(passCell(s.pass), el(s.pass === false ? 'b' : 'span', null, `${s.criterion} · ${s.name}`));
    if (s.pass === false || s.reason) cr.append(el('p', null, s.reason));
    return cr;
  }));
  $('eval-turns').replaceChildren(...turns.flatMap((t, i) => {
    const q = el('div', 'msg user', t.question);
    if (t.images.length) q.prepend(imageStrip(t.images));
    q.prepend(el('div', 'turn', `Lượt ${i + 1}`));
    const a = el('div', 'msg bot');
    a.innerHTML = md(t.answer);
    return [q, a];
  }));
  $('eval-rationale').innerHTML = md(ev.rationale);
  loadEval();
}

// ---- Token ----

const usageSources = [['chat', 'Chat', 'câu'], ['bench', 'Benchmark', 'câu'], ['eval', 'Eval chat thật', 'session']];
const usageDays = segValue('usage-days', () => loadUsage());
const windowNames = { '5h': '5 giờ', day: 'Ngày', week: 'Tuần', month: 'Tháng' };
const fmtPct = n => `${Math.round(n)}%`;
const levelClass = ratio => ratio > 0.5 ? 'ok' : ratio > 0.2 ? 'warn' : 'low';

function fmtLeft(t) {
  const m = Math.max(0, Math.round((new Date(t) - Date.now()) / 60000));
  if (m < 60) return `${m} phút`;
  if (m < 1440) return `${Math.floor(m / 60)} giờ ${m % 60} phút`;
  return `${Math.floor(m / 1440)} ngày ${Math.floor(m % 1440 / 60)} giờ`;
}

function meter(parts, cls = 'meter') {
  const m = el('div', cls);
  m.replaceChildren(...parts.map(([width, c]) => {
    const i = el('i', c);
    i.style.width = `${Math.max(0, Math.min(100, width))}%`;
    return i;
  }));
  return m;
}

async function loadQuota(refresh) {
  // On a cold cache the proxy queries every provider, which takes a few seconds
  if (!$('quota-groups').children.length) $('quota-groups').replaceChildren(el('p', 'note', 'Đang lấy hạn mức…'));
  let data;
  try { data = await call(`/api/admin/quota${refresh ? '?refresh=true' : ''}`); }
  catch (e) { data = { configured: true, report: { error: e.message, groups: [], accounts: [] } }; }
  const err = $('quota-error');
  if (!data.configured) {
    err.hidden = false;
    err.textContent = 'Chưa cấu hình CLIPROXY_MANAGEMENT_KEY trong .env nên không đọc được hạn mức.';
    $('quota-groups').replaceChildren();
    $('quota-accounts').replaceChildren();
    return;
  }
  const r = data.report;
  $('quota-model').textContent = r.model ? `Model ${r.model} qua CLIProxyAPI` : '';
  $('quota-time').textContent = r.fetchedAt ? `Lấy lúc ${fmtShort(r.fetchedAt)}` : '';
  err.hidden = !r.error;
  err.textContent = r.error ?? '';

  $('quota-groups').replaceChildren(...r.groups.map(g => {
    const box = el('div', 'qgroup');
    box.append(el('div', 'qtitle', `${g.provider} · ${g.plan ?? 'không rõ gói'} · ${g.accounts} tài khoản`));
    g.windows.forEach((w, i) => {
      const ratio = w.remaining / w.capacity;
      const reset = w.nextReset ? `hồi sớm nhất sau ${fmtLeft(w.nextReset)}` : 'chưa dùng';
      if (i === 0) {
        const big = box.appendChild(el('div', 'bignum', fmtPct(w.remaining)));
        big.append(' ', el('small', null, `còn lại / ${w.capacity}% · ${windowNames[w.kind] ?? w.kind}`));
        box.append(meter([[ratio * 100, levelClass(ratio)]]), el('div', 'note', reset));
      } else {
        const row = box.appendChild(el('div', 'qwin'));
        row.append(el('span', null, windowNames[w.kind] ?? w.kind), meter([[ratio * 100, levelClass(ratio)]], 'meter thin'),
          el('b', null, `${fmtPct(w.remaining)} / ${w.capacity}%`), el('span', 'note', reset));
      }
    });
    return box;
  }));

  $('quota-accounts').replaceChildren(...r.accounts.map(a => {
    const row = el('div', a.usable ? 'acc' : 'acc off');
    row.append(el('span', 'name', a.name), el('span', null, a.windows.map(w => `${windowNames[w.kind] ?? w.kind} ${fmtPct(w.remaining)}`).join(' · ') || '—'));
    const status = a.error ?? (a.usable ? null : a.status ?? 'tạm tắt');
    if (status) row.append(el('span', 'note', status));
    return row;
  }));
}

$('quota-refresh').addEventListener('click', () => loadQuota(true));

// "jane.doe@example.com" → "JD"
function emailInitials(name) {
  const words = name.split('@')[0].split(/[._\-\s]+/).filter(Boolean);
  return ((words[0]?.[0] ?? '?') + (words.length > 1 ? words.at(-1)[0] : '')).toUpperCase();
}

function totalCard(title, period, foot) {
  const total = Object.values(period.bySource).reduce((n, s) => n + s.tokens, 0);
  const box = [el('h2', null, title), el('div', 'bignum', fmtTok(total))];
  box[1].title = `${fmtNum(total)} token`;
  box.push(meter(usageSources.map(([key]) => [total ? (period.bySource[key]?.tokens ?? 0) / total * 100 : 0, `src-${key}`])));
  const rows = el('div', 'rows3');
  for (const [key, name, unit] of usageSources) {
    const s = period.bySource[key] ?? { count: 0, tokens: 0 };
    const line = rows.appendChild(el('div'));
    line.append(el('i', `sw src-${key}`), name, el('small', null, `${fmtNum(s.count)} ${unit}`));
    const b = line.appendChild(el('b', null, fmtTok(s.tokens)));
    b.title = `${fmtNum(s.tokens)} token`;
  }
  box.push(rows, el('p', 'foot', foot));
  return box;
}

async function loadUsage() {
  loadQuota(false);
  const days = usageDays();
  let data;
  try { data = await call(`/api/admin/usage?days=${days}`); }
  catch (e) { return alert(e.message); }

  const [today, week, month] = data.totals;
  const weekTotal = Object.values(week.bySource).reduce((n, s) => n + s.tokens, 0);
  $('usage-today').replaceChildren(...totalCard('Token hôm nay', today, 'Benchmark chưa tính token judge (thực tế khoảng gấp đôi).'));
  $('usage-month').replaceChildren(...totalCard('30 ngày', month, `7 ngày: ${fmtTok(weekTotal)} · trung bình ${fmtTok(Math.round(weekTotal / 7))}/ngày`));

  $('usage-legend').replaceChildren(...usageSources.map(([key, name]) => {
    const s = el('span', null, name);
    s.prepend(el('i', `sw src-${key}`));
    return s;
  }));
  renderUsageChart(data.daily, days);

  if (document.activeElement !== $('quota-default')) $('quota-default').value = data.defaultQuota;

  $('usage-users-n').textContent = `Câu ${days} ngày`;
  $('usage-users-t').textContent = `Token ${days} ngày`;
  $('usage-users').replaceChildren(...(data.users.length ? data.users.map(u => {
    const limit = u.exempt ? null : u.customQuota ?? data.defaultQuota;
    const over = limit != null && u.todayCount >= limit;
    const tr = el('tr', over ? 'static over' : 'static');

    const person = tr.appendChild(el('td')).appendChild(el('div', 'person'));
    person.append(el('span', 'avatar', emailInitials(u.userName)), u.userName);
    if (over) person.append(el('span', 'tag red', 'hết lượt'));

    const use = tr.appendChild(el('td')).appendChild(el('div', 'useb'));
    const ratio = limit ? u.todayCount / limit : 0;
    use.append(meter([[ratio * 100, ratio >= 1 ? 'low' : ratio >= 0.7 ? 'warn' : 'ok']]),
      el('span', null, limit != null ? `${fmtNum(u.todayCount)}/${fmtNum(limit)}` : fmtNum(u.todayCount)));

    for (const [v, title] of [[fmtTok(u.todayTokens), u.todayTokens], [fmtNum(u.count), null], [fmtTok(u.tokens), u.tokens]]) {
      const td = tr.appendChild(el('td', 'num', v));
      if (title != null) td.title = `${fmtNum(title)} token`;
    }
    tr.append(el('td', null, u.exempt ? 'Không giới hạn' : `${fmtNum(limit)} · ${u.customQuota != null ? 'riêng' : 'mặc định'}`));
    tr.append(el('td', null, u.lastAt ? fmtShort(u.lastAt) : '—'));
    const td = tr.appendChild(el('td'));
    if (!u.exempt) {
      const edit = td.appendChild(el('button', 'link-btn', 'Sửa'));
      edit.addEventListener('click', () => editUserQuota(u, data.defaultQuota));
    }
    return tr;
  }) : [Object.assign(el('tr', 'static'), { innerHTML: `<td colspan="8" class="note">Chưa có ai chat trong ${days} ngày.</td>` })]));
}

// The quota API returns 204 with no body, so call() (which always parses JSON) is not used
async function put(url, body) {
  const res = await fetch(url, { method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) });
  if (!res.ok) throw new Error((await res.text()) || res.statusText);
}

async function editUserQuota(u, defaultQuota) {
  const input = prompt(
    `Số câu ${u.userName} được hỏi mỗi ngày.\nĐể trống = dùng mức mặc định ${defaultQuota} câu.`,
    u.customQuota ?? '');
  if (input === null) return;
  const questions = input.trim() === '' ? null : Number(input.trim());
  if (questions !== null && !(Number.isInteger(questions) && questions >= 0)) return alert('Cần số nguyên ≥ 0');
  try {
    await put(`/api/admin/quotas/users/${encodeURIComponent(u.userName)}`, { questions });
    loadUsage();
  } catch (e) { alert(e.message); }
}

$('quota-form').addEventListener('submit', async e => {
  e.preventDefault();
  try {
    await put('/api/admin/quotas/default', { questions: Number($('quota-default').value) });
    $('quota-default').blur();
    loadUsage();
  } catch (err) { alert(err.message); }
});

// Stacked bars by source, hand-drawn like the eval chart
function renderUsageChart(daily, days) {
  const W = 900, H = 220, L = 48, R = 8, T = 10, B = 24;
  const ns = 'http://www.w3.org/2000/svg';
  const svg = document.createElementNS(ns, 'svg');
  svg.setAttribute('viewBox', `0 0 ${W} ${H}`);
  const add = (tag, attrs, text) => {
    const e = svg.appendChild(document.createElementNS(ns, tag));
    for (const [k, v] of Object.entries(attrs)) e.setAttribute(k, v);
    if (text != null) e.textContent = text;
    return e;
  };

  const start = new Date();
  start.setHours(0, 0, 0, 0);
  start.setDate(start.getDate() - (days - 1));
  const dayIndex = s => Math.round((new Date(`${s}T00:00:00`) - start) / 86400000);
  const slot = (W - L - R) / days;
  const total = d => Object.values(d.bySource).reduce((n, v) => n + v, 0);
  const max = Math.max(1, ...daily.map(total));
  // Round the axis max up to 1, 2 or 5 × 10^k for readability
  const mag = 10 ** Math.floor(Math.log10(max));
  const top = [1, 2, 5, 10].map(m => m * mag).find(v => v >= max);
  const y = v => T + (top - v) * (H - T - B) / top;

  for (const v of [0, top / 4, top / 2, top * 3 / 4, top]) {
    add('line', { x1: L, x2: W - R, y1: y(v), y2: y(v), class: 'grid' });
    add('text', { x: L - 6, y: y(v) + 4, class: 'axis', 'text-anchor': 'end' }, fmtTok(v));
  }
  const label = i => { const d = new Date(start); d.setDate(d.getDate() + i); return `${d.getDate()}/${d.getMonth() + 1}`; };
  const ticks = [...new Set([0, Math.round((days - 1) / 4), Math.round((days - 1) / 2), Math.round((days - 1) * 3 / 4), days - 1])];
  for (const i of ticks) {
    add('text', { x: L + (i + 0.5) * slot, y: H - 6, class: 'axis', 'text-anchor': 'middle' }, label(i));
  }

  for (const d of daily) {
    const i = dayIndex(d.date);
    if (i < 0 || i >= days) continue;
    let base = 0;
    for (const [key, name] of usageSources) {
      const v = d.bySource[key];
      if (!v) continue;
      const r = add('rect', { x: L + i * slot + slot * 0.18, width: slot * 0.64, y: y(base + v), height: y(base) - y(base + v), class: `src-${key}` });
      r.appendChild(document.createElementNS(ns, 'title')).textContent = `${d.date} · ${name}: ${fmtNum(v)} token`;
      base += v;
    }
  }
  $('usage-chart').replaceChildren(svg);
}

call(docsApi).then(renderDocs).catch(e => alert(e.message));
