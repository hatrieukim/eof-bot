// Turns "(Nguồn: a.md § 7.4–7.6; b.md § 3)" in an answer into clickable numbers [1] [2]…
// Parsing matches Citations.ParseSources on the server (ranges 7.4–7.6 expand to 7.4, 7.5, 7.6)

const CITE_RE = /\(Nguồn:\s*([^)]*)\)/g;
const RANGE_RE = /§\s*([\d.]+)\s*[–-]\s*([\d.]+)/g;
const SECTION_RE = /§\s*([\d.]+)/g;

const trimDot = s => s.replace(/\.+$/, '');

function expandRange(a, b) {
  const ia = a.lastIndexOf('.'), ib = b.lastIndexOf('.');
  if (ia > 0 && ib > 0 && a.slice(0, ia) === b.slice(0, ib)) {
    const x = Number(a.slice(ia + 1)), y = Number(b.slice(ib + 1));
    if (Number.isInteger(x) && Number.isInteger(y) && y >= x) {
      return Array.from({ length: y - x + 1 }, (_, i) => `${a.slice(0, ia)}.${x + i}`);
    }
  }
  if (/^\d+$/.test(a) && /^\d+$/.test(b) && Number(b) >= Number(a)) {
    return Array.from({ length: Number(b) - Number(a) + 1 }, (_, i) => String(Number(a) + i));
  }
  return [a, b];
}

// section = null when the bot only cited a file name
function parseRefs(text) {
  const refs = [];
  let file = null;
  for (const part of text.split(/[;,]/)) {
    const f = part.match(/([\w-]+\.md)/);
    if (f) file = f[1];
    if (!file) continue;
    const sections = [];
    for (const m of part.matchAll(RANGE_RE)) sections.push(...expandRange(trimDot(m[1]), trimDot(m[2])));
    for (const m of part.replace(RANGE_RE, '').matchAll(SECTION_RE)) sections.push(trimDot(m[1]));
    if (sections.length) for (const s of sections) refs.push({ file, section: s });
    else if (f) refs.push({ file, section: null });
  }
  return refs;
}

// Returns the markdown with source blocks replaced by number tags, and the unique sources in order of appearance
function linkCitations(text) {
  const refs = [];
  const index = new Map();
  const out = text.replace(CITE_RE, (whole, body) => {
    const nums = parseRefs(body).map(r => {
      const key = `${r.file}|${r.section ?? ''}`;
      if (!index.has(key)) {
        index.set(key, refs.length);
        refs.push(r);
      }
      return index.get(key);
    });
    if (!nums.length) return whole;
    return `<span class="cites">${[...new Set(nums)].map(i => `<a class="n" href="#" data-ref="${i}">${i + 1}</a>`).join('')}</span>`;
  });
  return { text: out, refs };
}

const sectionCache = new Map();

// null = section not found (wrong section number, or the document changed); network/server errors throw and are not cached so clicking again retries
function fetchSection(ref) {
  const key = `${ref.file}|${ref.section}`;
  if (!sectionCache.has(key)) {
    const url = `/api/docs/section?file=${encodeURIComponent(ref.file)}&section=${encodeURIComponent(ref.section)}`;
    const p = fetch(url).then(r => {
      if (r.ok) return r.json();
      if (r.status === 404) return null;
      throw new Error(`HTTP ${r.status}`);
    });
    p.catch(() => sectionCache.delete(key));
    sectionCache.set(key, p);
  }
  return sectionCache.get(key);
}
