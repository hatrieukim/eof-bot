// Image tokens depend on pixel count, not file size: to save tokens, shrink the dimensions.
// 1568px: a Full HD screenshot keeps ~82%, 12px text becomes ~10px and stays readable
const IMAGE_MAX_SIDE = 1568;
// Already-small images keep the original file, avoiding recompression that blurs text
const IMAGE_KEEP_BYTES = 1024 * 1024;
// PNG is lossless (good for text-heavy screenshots); above this size it is usually a photo → JPEG
const IMAGE_PNG_MAX_BYTES = 800 * 1024;
const IMAGE_JPEG_QUALITY = 0.85;
const IMAGE_KEEP_TYPES = ['image/png', 'image/jpeg', 'image/webp'];

function blobToDataUrl(blob) {
  return new Promise((resolve, reject) => {
    const r = new FileReader();
    r.onload = () => resolve(r.result);
    r.onerror = () => reject(r.error);
    r.readAsDataURL(blob);
  });
}

function canvasToBlob(canvas, type, quality) {
  return new Promise((resolve) => canvas.toBlob(resolve, type, quality));
}

// Returns { dataUrl, width, height, bytes, original: { width, height, bytes, type } }
async function compressImage(file) {
  let bitmap;
  try { bitmap = await createImageBitmap(file); }
  catch { throw new Error(`Không đọc được ảnh (${file.type || 'không rõ định dạng'})`); }

  const original = { width: bitmap.width, height: bitmap.height, bytes: file.size, type: file.type };
  const scale = Math.min(1, IMAGE_MAX_SIDE / Math.max(bitmap.width, bitmap.height));

  if (scale === 1 && file.size <= IMAGE_KEEP_BYTES && IMAGE_KEEP_TYPES.includes(file.type)) {
    bitmap.close();
    return { dataUrl: await blobToDataUrl(file), width: original.width, height: original.height, bytes: file.size, original };
  }

  const canvas = document.createElement('canvas');
  canvas.width = Math.round(bitmap.width * scale);
  canvas.height = Math.round(bitmap.height * scale);
  const g = canvas.getContext('2d');
  // JPEG has no transparency; without a white fill transparent areas turn black
  g.fillStyle = '#fff';
  g.fillRect(0, 0, canvas.width, canvas.height);
  g.imageSmoothingQuality = 'high';
  g.drawImage(bitmap, 0, 0, canvas.width, canvas.height);
  bitmap.close();

  let blob = await canvasToBlob(canvas, 'image/png');
  if (blob.size > IMAGE_PNG_MAX_BYTES) blob = await canvasToBlob(canvas, 'image/jpeg', IMAGE_JPEG_QUALITY);
  return { dataUrl: await blobToDataUrl(blob), width: canvas.width, height: canvas.height, bytes: blob.size, original };
}

// Matches ChatImages.MaxCount on the server
const IMAGE_MAX_COUNT = 4;

function showLightbox(src) {
  let box = document.querySelector('.lightbox');
  if (!box) {
    box = document.body.appendChild(document.createElement('div'));
    box.className = 'lightbox';
    box.appendChild(document.createElement('img'));
    box.onclick = () => { box.hidden = true; };
    document.addEventListener('keydown', (e) => { if (e.key === 'Escape') box.hidden = true; });
  }
  box.querySelector('img').src = src;
  box.hidden = false;
}

// Thumbnail strip in a user message; click for full size
function imageStrip(srcs) {
  const strip = document.createElement('div');
  strip.className = 'img-strip';
  for (const src of srcs) {
    const img = strip.appendChild(document.createElement('img'));
    img.src = src;
    img.alt = 'Ảnh đính kèm';
    img.onclick = () => showLightbox(src);
  }
  return strip;
}

// Wires paste / drag-and-drop / file picker to a chat box. Returns { images(), busy(), clear() }
function attachImages({ form, input, button, list, error }) {
  let items = [];
  let pending = 0;

  const file = document.createElement('input');
  file.type = 'file';
  file.accept = 'image/*';
  file.multiple = true;
  file.hidden = true;
  form.append(file);

  function showError(text) {
    error.textContent = text;
    error.hidden = !text;
  }

  function render() {
    list.hidden = items.length === 0 && pending === 0;
    list.replaceChildren(...items.map((item) => {
      const box = document.createElement('div');
      box.className = 'thumb';
      const img = box.appendChild(document.createElement('img'));
      img.src = item.dataUrl;
      img.title = `${item.width}×${item.height}, ${Math.ceil(item.bytes / 1024)}KB`;
      img.onclick = () => showLightbox(item.dataUrl);
      const x = box.appendChild(document.createElement('button'));
      x.type = 'button';
      x.textContent = '×';
      x.setAttribute('aria-label', 'Bỏ ảnh');
      x.onclick = () => { items = items.filter((i) => i !== item); showError(''); render(); };
      return box;
    }), ...Array.from({ length: pending }, () => {
      const box = document.createElement('div');
      box.className = 'thumb loading';
      return box;
    }));
  }

  async function add(files) {
    const images = [...files].filter((f) => f.type.startsWith('image/'));
    if (!images.length) return;
    showError('');
    const room = IMAGE_MAX_COUNT - items.length - pending;
    if (images.length > room) showError(`Mỗi câu gửi tối đa ${IMAGE_MAX_COUNT} ảnh.`);
    const accepted = images.slice(0, Math.max(0, room));
    pending += accepted.length;
    render();
    await Promise.all(accepted.map(async (f) => {
      try { items.push(await compressImage(f)); }
      catch (e) { showError(e.message); }
      finally { pending--; render(); }
    }));
  }

  // Copying text from Word/Excel also puts an image on the clipboard: if there is text, paste the text
  input.addEventListener('paste', (e) => {
    if (e.clipboardData.getData('text/plain')) return;
    if (![...e.clipboardData.files].some((f) => f.type.startsWith('image/'))) return;
    e.preventDefault();
    add(e.clipboardData.files);
  });

  form.addEventListener('dragover', (e) => {
    if (![...e.dataTransfer.types].includes('Files')) return;
    e.preventDefault();
    form.classList.add('dragging');
  });
  form.addEventListener('dragleave', (e) => {
    if (!form.contains(e.relatedTarget)) form.classList.remove('dragging');
  });
  form.addEventListener('drop', (e) => {
    form.classList.remove('dragging');
    if (!e.dataTransfer.files.length) return;
    e.preventDefault();
    add(e.dataTransfer.files);
  });

  // A drop outside the chat box would make the browser open the image and lose the chat page
  for (const type of ['dragover', 'drop']) {
    window.addEventListener(type, (e) => { if (!form.contains(e.target)) e.preventDefault(); });
  }

  button.onclick = () => file.click();
  file.onchange = () => { add(file.files); file.value = ''; };

  render();
  return {
    images: () => items.map((i) => i.dataUrl),
    busy: () => pending > 0,
    clear() { items = []; showError(''); render(); },
  };
}
