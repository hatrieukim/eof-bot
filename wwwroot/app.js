const $ = (s) => document.querySelector(s);

let sessionId = null;
let busy = false;
let firstName = '';

// If the sign-in cookie expires mid-session the API returns 401: go back to the login page
async function api(url, options) {
  const res = await fetch(url, options);
  if (res.status === 401) {
    location.href = '/login';
    throw new Error('Phiên đăng nhập đã hết hạn');
  }
  return res;
}

// Answers are model-generated, so sanitize the HTML before inserting it
function md(text) { return DOMPurify.sanitize(marked.parse(text)); }

function scrollToEnd() { $('#messages').scrollTop = $('#messages').scrollHeight; }

function setHome(on) { $('#main').classList.toggle('home', on); }

function addMessage(role, text, images) {
  setHome(false);
  const div = document.createElement('div');
  div.className = `msg ${role}`;
  if (role === 'bot') renderAnswer(div, text);
  else {
    if (images?.length) div.append(imageStrip(images));
    if (text) div.append(text);
  }
  $('#messages').append(div);
  scrollToEnd();
  return div;
}

function renderAnswer(div, text) {
  const { text: linked, refs } = linkCitations(text);
  div.innerHTML = md(linked);
  div.refs = refs;
  if (refs.length) div.append(refList(refs));
}

function refList(refs) {
  const list = document.createElement('div');
  list.className = 'refs';
  refs.forEach((ref, i) => {
    const b = list.appendChild(document.createElement('button'));
    b.type = 'button';
    b.className = 'ref';
    b.dataset.ref = i;
    const n = b.appendChild(document.createElement('span'));
    n.className = 'n';
    n.textContent = i + 1;
    const title = b.appendChild(document.createElement('span'));
    title.className = 't';
    title.textContent = ref.section ? `§ ${ref.section}` : 'Cả file';
    const file = b.appendChild(document.createElement('code'));
    file.textContent = ref.file;
    if (ref.section) {
      fetchSection(ref).then(s => {
        if (s) title.textContent = s.title;
        else b.classList.add('missing');
      }, () => {});
    }
  });
  return list;
}

function addMeta(after, r) {
  const p = document.createElement('p');
  p.className = 'meta';
  const tokens = r.inputTokens == null ? '' : ` · ${r.inputTokens} in / ${r.cachedTokens ?? 0} cached / ${r.outputTokens} out`;
  p.textContent = `${(r.latencyMs / 1000).toFixed(1)}s${tokens}`;
  after.after(p);
}

function clearMessages() {
  $('#messages').replaceChildren();
  closeSource();
}

function setTitle(text) { $('#title').textContent = text || 'Cuộc trò chuyện mới'; }

// Initials of the first and last word of the name ("Hoàng Thùy Linh" → "HL")
function initials(name) {
  const words = name.trim().split(/\s+/);
  return (words[0][0] + (words.length > 1 ? words.at(-1)[0] : '')).toUpperCase();
}

function greeting() {
  const h = new Date().getHours();
  const part = h < 11 ? 'buổi sáng' : h < 13 ? 'buổi trưa' : h < 18 ? 'buổi chiều' : 'buổi tối';
  return `Chào ${part}${firstName ? `, ${firstName}` : ''}`;
}

function fitInput() {
  const input = $('#input');
  input.style.height = 'auto';
  input.style.height = `${input.scrollHeight}px`;
}

async function loadQuota() {
  const q = await (await api('/api/me/quota')).json();
  $('#quota').hidden = q.exempt;
  if (q.exempt) return;
  const left = Math.max(0, q.limit - q.used);
  $('#quota').textContent = `Hôm nay còn ${left} trong ${q.limit} câu`;
  $('#quota').classList.toggle('out', left === 0);
}

async function loadSessions() {
  const res = await api('/api/sessions');
  const list = await res.json();
  $('#sessions').replaceChildren(...list.map((s) => {
    const li = document.createElement('li');
    li.textContent = s.title;
    li.title = `${s.title}\n${new Date(s.updatedAt).toLocaleString('vi-VN')}`;
    li.classList.toggle('active', s.id === sessionId);
    if (s.id === sessionId) setTitle(s.title);
    li.onclick = () => openSession(s.id);
    return li;
  }));
}

async function openSession(id) {
  if (busy) return;
  sessionId = id;
  const res = await api(`/api/sessions/${id}/messages`);
  clearMessages();
  for (const m of await res.json()) {
    addMessage('user', m.question, m.images);
    addMessage('bot', m.answer);
  }
  loadSessions();
}

function newChat() {
  if (busy) return;
  sessionId = null;
  attach.clear();
  clearMessages();
  setTitle();
  $('#greeting').textContent = greeting();
  setHome(true);
  loadSessions();
  $('#input').focus();
}

function notice(bot, text) {
  bot.classList.add('notice');
  bot.textContent = text;
}

async function send(text, images) {
  busy = true;
  $('#send').disabled = true;
  addMessage('user', text, images);
  const bot = addMessage('bot', '');
  bot.classList.add('pending');
  let answer = '';
  try {
    const res = await api('/api/chat/stream', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ sessionId, message: text, images }),
    });
    // 429 = the user's quota for today is used up; the server provides the message
    if (res.status === 429) return notice(bot, await res.text());
    if (!res.ok) throw new Error(await res.text());
    for await (const { event, data } of readSse(res)) {
      if (event === 'delta') {
        answer += data.text;
        renderAnswer(bot, answer);
        scrollToEnd();
      } else if (event === 'done') {
        sessionId = data.sessionId;
        addMeta(bot, data);
      } else if (event === 'error') {
        if (data.quota) return notice(bot, data.message);
        throw new Error(data.message);
      }
    }
  } catch (e) {
    bot.classList.add('error');
    bot.textContent = `Lỗi: ${e.message}`;
  } finally {
    bot.classList.remove('pending');
    busy = false;
    $('#send').disabled = false;
    loadSessions();
    loadQuota();
  }
}

// ---- Source panel ----

let sourceMsg = null;

function markActive(i) {
  for (const a of document.querySelectorAll('#messages .on')) a.classList.remove('on');
  if (!sourceMsg) return;
  for (const a of sourceMsg.querySelectorAll(`[data-ref="${i}"]`)) a.classList.add('on');
}

async function openSource(msg, i) {
  sourceMsg = msg;
  const ref = msg.refs[i];
  $('#source').hidden = false;
  markActive(i);
  $('#src-tabs').replaceChildren(...msg.refs.map((_, k) => {
    const b = document.createElement('button');
    b.type = 'button';
    b.textContent = k + 1;
    b.classList.toggle('on', k === i);
    b.onclick = () => openSource(msg, k);
    return b;
  }));

  const body = $('#src-body');
  if (!ref.section) {
    $('#src-crumb').textContent = ref.file;
    body.innerHTML = '<p class="src-note">Bot chỉ ghi tên file, không ghi số mục.</p>';
    return;
  }
  body.innerHTML = '<p class="src-note">Đang tải…</p>';
  let s;
  try { s = await fetchSection(ref); }
  catch (e) {
    if (sourceMsg !== msg) return;
    $('#src-crumb').textContent = `${ref.file} › § ${ref.section}`;
    body.innerHTML = `<p class="src-note">Không tải được nội dung mục (${e.message}). Bấm lại số nguồn để thử lại.</p>`;
    return;
  }
  if (sourceMsg !== msg) return;
  if (!s) {
    $('#src-crumb').textContent = `${ref.file} › § ${ref.section}`;
    body.innerHTML = `<p class="src-note">Không tìm thấy § ${ref.section} trong ${ref.file} của bản tài liệu hiện tại. Có thể bot ghi sai số mục, hoặc tài liệu đã được sửa sau khi trả lời.</p>`;
    return;
  }
  $('#src-crumb').textContent = [ref.file, s.parent, s.title].filter(Boolean).join(' › ');
  const h = document.createElement('h3');
  h.textContent = s.title;
  body.replaceChildren(h);
  body.insertAdjacentHTML('beforeend', md(s.content));
  body.scrollTop = 0;
}

function closeSource() {
  sourceMsg = null;
  $('#source').hidden = true;
  markActive();
}

$('#messages').addEventListener('click', (e) => {
  const target = e.target.closest('[data-ref]');
  if (!target) return;
  e.preventDefault();
  openSource(target.closest('.msg'), Number(target.dataset.ref));
});

$('#src-close').onclick = closeSource;

// ---- Question input ----

const attach = attachImages({
  form: $('#ask'), input: $('#input'), button: $('#attach'), list: $('#attachments'), error: $('#attach-error'),
});

$('#ask').addEventListener('submit', (e) => {
  e.preventDefault();
  const text = $('#input').value.trim();
  const images = attach.images();
  if ((!text && !images.length) || busy || attach.busy()) return;
  $('#input').value = '';
  attach.clear();
  fitInput();
  send(text, images);
});

$('#input').addEventListener('keydown', (e) => {
  if (e.key === 'Enter' && !e.shiftKey && !e.isComposing) {
    e.preventDefault();
    $('#ask').requestSubmit();
  }
});

$('#input').addEventListener('input', fitInput);

// Suggestions only prefill the input so users can edit before sending and do not waste a question on a misclick
for (const b of document.querySelectorAll('#suggest button')) {
  b.onclick = () => {
    $('#input').value = b.textContent;
    fitInput();
    $('#input').focus();
  };
}

$('#new-chat').onclick = newChat;

(async () => {
  const me = await (await api('/api/me')).json();
  const name = me.anonymous ? 'Khách' : me.name ?? me.email;
  $('#user-name').textContent = name;
  $('#user-name').title = me.email;
  $('#avatar').textContent = initials(name);
  $('#admin-link').hidden = !me.isAdmin;
  $('#logout').hidden = me.anonymous;
  // Vietnamese names: the given name is the last word ("Hoàng Thùy Linh" → "Linh")
  firstName = me.name ? me.name.trim().split(/\s+/).at(-1) : '';
  newChat();
  loadQuota();
})();
