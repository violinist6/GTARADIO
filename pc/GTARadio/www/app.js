const COLORS = {};
let stations = [];
let current = null; // station id or null (off)
let lastOn = null;
let ctx, node, gain, ctrl, gen = 0;

const wheel = document.getElementById('wheel');
const els = {};

async function init() {
  stations = await (await fetch('/api/stations')).json();
  const n = stations.length + 1;
  const items = [{ id: null, name: 'Radio Off' }, ...stations];
  // Buttons sit on a ring and are sized so neighbours never touch, however many stations there are.
  const R = 42;
  const size = Math.min(14, 0.86 * (2 * Math.PI * R) / n);
  wheel.style.setProperty('--btn', size + '%');
  items.forEach((s, i) => {
    const a = (i / n) * Math.PI * 2 - Math.PI / 2;
    const b = document.createElement('button');
    b.className = 'station' + (s.id ? '' : ' off-btn');
    b.style.left = (50 + R * Math.cos(a)) + '%';
    b.style.top = (50 + R * Math.sin(a)) + '%';
    if (s.icon) {
      const img = document.createElement('img');
      img.src = '/icons/' + s.id + '.png'; img.alt = s.name;
      img.onerror = () => { img.remove(); b.classList.remove('has-icon'); label(b, s.name); fitLabels(); };
      b.classList.add('has-icon');
      b.appendChild(img);
    } else label(b, s.name);
    b.title = s.name;
    b.setAttribute('aria-label', s.name);
    b.onclick = () => tune(s.id);
    wheel.appendChild(b);
    els[s.id ?? 'off'] = b;
  });
  fitLabels();
  window.addEventListener('resize', fitLabels);
  const cfg = await (await fetch('/api/settings')).json();
  document.getElementById('news').checked = cfg.news;
  render();
  setInterval(refresh, 1000);
}

function label(b, text) { const sp = document.createElement('span'); sp.textContent = text; b.replaceChildren(sp); }

// Shrink each name until whole words fit inside its circle.
function fitLabels() {
  for (const b of wheel.querySelectorAll('.station:not(.has-icon)')) {
    const sp = b.querySelector('span'); if (!sp) continue;
    let size = Math.min(12, b.clientWidth / 5.5);
    const room = b.clientWidth * 0.78;
    do { sp.style.fontSize = size + 'px'; size -= 0.5; } while (size >= 6 && (sp.scrollWidth > sp.clientWidth || sp.offsetHeight > room));
  }
}

async function startAudio() {
  ctx = new AudioContext({ sampleRate: 48000 });
  await ctx.audioWorklet.addModule('worklet.js');
  node = new AudioWorkletNode(ctx, 'pcm-player', { numberOfInputs: 0, outputChannelCount: [2] });
  gain = ctx.createGain();
  gain.gain.value = +document.getElementById('vol').value;
  node.connect(gain).connect(ctx.destination);
}

async function tune(id) {
  if (ctrl) { ctrl.abort(); ctrl = null; }
  const myGen = ++gen;
  node.port.postMessage({ type: 'flush', gen: myGen });
  current = id;
  if (id) lastOn = id;
  render();
  if (!id) return;
  const c = new AbortController(); ctrl = c;
  try {
    const res = await fetch('/live/' + id, { signal: c.signal });
    const reader = res.body.getReader();
    let carry = null;
    while (true) {
      const { value, done } = await reader.read();
      if (done || c.signal.aborted || myGen !== gen) break;
      let bytes = value;
      if (carry) { const m = new Uint8Array(carry.length + bytes.length); m.set(carry); m.set(bytes, carry.length); bytes = m; carry = null; }
      const usable = bytes.length - (bytes.length % 2);
      if (usable < bytes.length) carry = bytes.slice(usable);
      const pcm = new Int16Array(bytes.buffer.slice(bytes.byteOffset, bytes.byteOffset + usable));
      const f = new Float32Array(pcm.length);
      for (let i = 0; i < pcm.length; i++) f[i] = pcm[i] / 32768;
      node.port.postMessage({ type: 'pcm', data: f, gen: myGen }, [f.buffer]);
    }
  } catch (e) { /* switched station or server closed */ }
}

function step(dir) {
  const ids = [null, ...stations.map(s => s.id)];
  const i = ids.indexOf(current);
  tune(ids[(i + dir + ids.length) % ids.length]);
}

async function refresh() {
  try { stations = await (await fetch('/api/stations')).json(); render(); } catch (e) {}
}

function render() {
  for (const [k, b] of Object.entries(els)) b.classList.toggle('on', (current ?? 'off') === k);
  const s = stations.find(x => x.id === current);
  const name = document.getElementById('st-name'), t = document.getElementById('np-title'), a = document.getElementById('np-artist'), tag = document.getElementById('np-tag');
  if (!s) { name.textContent = 'Radio Off'; t.textContent = ''; a.textContent = ''; tag.classList.remove('show'); return; }
  name.textContent = s.name;
  const np = s.now || {};
  t.textContent = np.title || (s.kind === 'mix' ? 'Live mix' : '');
  a.textContent = np.artist || '';
  tag.textContent = np.dj ? 'DJ on air' : np.kind === 'ad' ? 'Advert' : np.kind === 'news' ? 'News' : '';
  tag.classList.toggle('show', !!(np.dj || np.kind === 'ad' || np.kind === 'news'));
}

document.getElementById('start-btn').onclick = async () => {
  document.getElementById('start').remove();
  await startAudio();
  const pick = stations[Math.floor(Math.random() * stations.length)];
  tune(pick.id);
};
document.getElementById('vol').oninput = (e) => { if (gain) gain.gain.value = +e.target.value; };
document.getElementById('news').onchange = (e) => fetch('/api/settings?news=' + (e.target.checked ? 1 : 0));
document.addEventListener('keydown', (e) => {
  if (!node) return;
  if (e.key === 'ArrowRight' || e.key.toLowerCase() === 'e') step(1);
  else if (e.key === 'ArrowLeft' || e.key.toLowerCase() === 'q') step(-1);
  else if (e.key === ' ') { e.preventDefault(); tune(current ? null : (lastOn || stations[0].id)); }
});
// The iPhone copy: compressed audio plus the station list in one file next to GTARadio.exe.
async function phoneStatus(start) {
  const st = await (await fetch('/api/phone' + (start ? '?start=1' : ''))).json();
  const btn = document.getElementById('phone-btn'), msg = document.getElementById('phone-msg');
  btn.disabled = st.state === 'running';
  if (st.state === 'running') { msg.textContent = `Making the iPhone copy... ${Math.floor(100 * st.done / Math.max(1, st.total))}%`; setTimeout(phoneStatus, 2000); }
  else if (st.state === 'failed') msg.textContent = 'That didn\'t work: ' + st.error;
  else if (st.bytes > 0) { msg.textContent = `Ready: GTARadio-phone.pack (${(st.bytes / 1e9).toFixed(1)} GB) next to GTARadio.exe`; btn.textContent = 'Make it again'; }
}
document.getElementById('phone-btn').onclick = () => phoneStatus(true);
phoneStatus(false);
init();
