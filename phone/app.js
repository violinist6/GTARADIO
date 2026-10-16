// The phone radio: setup (copying the pack in), the station wheel, and the playback loop.
const LEAD = 30 * RATE;     // how far ahead of "now" the page renders audio
const BLOCK = RATE / 2;     // render half a second at a time

// Short labels so names fit the small circles on a phone; the full name shows in the middle.
const SHORT = {
  'Los Santos Rock Radio': 'ROCK', 'Non-Stop-Pop FM': 'POP', 'Radio Los Santos': 'RLS', 'Channel X': 'X',
  'West Coast Talk Radio': 'WCTR', 'Rebel Radio': 'REBEL', 'Soulwax FM': 'SWAX', 'East Los FM': 'EAST',
  'West Coast Classics': 'WCC', 'Blaine County Radio': 'BCR', 'The Blue Ark': 'ARK', 'Worldwide FM': 'WWFM',
  'FlyLo FM': 'FLYLO', 'The Lowdown 91.1': 'LOW', 'Radio Mirror Park': 'RMP', 'Space 103.2': 'SPACE',
  'Vinewood Boulevard Radio': 'VBR', 'Blonded Los Santos 97.8 FM': 'BLOND', 'Los Santos Underground Radio': 'LSUR',
  'iFruit Radio': 'iFRUIT', 'Still Slipping Los Santos': 'SLIP', 'Kult FM 99.1': 'KULT', 'The Music Locker': 'MLR',
  'MOTOMAMI Los Santos': 'MOTO', 'The Lab': 'LAB', 'Self Radio': 'SELF',
};
const shortName = n => SHORT[n] || n.replace(/\b(the|fm|radio|los santos)\b/gi, '').replace(/[\d.]+/g, '').trim().split(/\s+/).map(w => w[0]).join('').toUpperCase().slice(0, 4) || n.slice(0, 4).toUpperCase();

const cfg = { news: false };
try { cfg.news = localStorage.getItem('news') === '1'; } catch (e) {}

let stations = [], runners = {}, mixer = new Mixer();
let current = null, lastOn = null, gen = 0;
let ctx, node;
const wheel = document.getElementById('wheel');
const els = {};
const $ = id => document.getElementById(id);

async function boot() {
  if ('serviceWorker' in navigator) navigator.serviceWorker.register('sw.js').catch(() => {});
  const standalone = navigator.standalone || matchMedia('(display-mode: standalone)').matches;
  if (/iPhone|iPad|iPod/.test(navigator.userAgent) && !standalone) $('add-home').hidden = false;
  if (await Store.open()) showRadio(); else showSetup();
}

function showSetup(msg) {
  $('radio').hidden = true; $('start').hidden = true; $('setup').hidden = false;
  $('setup-msg').textContent = msg || '';
}

$('pick').onchange = async (e) => {
  const file = e.target.files[0];
  if (!file) return;
  $('pick-label').classList.add('busy'); $('progress').hidden = false;
  $('setup-msg').textContent = 'Copying the radio into this app. Keep this screen open...';
  try {
    await Store.import(file, p => { $('bar').style.width = (p * 100).toFixed(1) + '%'; $('setup-msg').textContent = `Copying the radio into this app: ${Math.floor(p * 100)}%. Keep this screen open.`; });
    showRadio();
  } catch (err) {
    showSetup(err.message);
  } finally {
    $('pick-label').classList.remove('busy'); $('progress').hidden = true; e.target.value = '';
  }
};

function showRadio() {
  $('setup').hidden = true; $('radio').hidden = false; $('start').hidden = false;
  stations = Store.index.stations.slice().sort((a, b) => a.wheel - b.wheel);
  Store.index.news = Store.index.news || [];
  const radio = { news: Store.index.news, minGap: Store.index.minGap || {} };
  const seed = (Date.now() & 0x7fffffff);
  runners = {};
  stations.forEach((s, i) => { runners[s.id] = new Runner(s, radio, cfg, seed ^ (i * 2654435761)); runners[s.id].ensure(Clock.now + 60 * RATE); });
  buildWheel();
  $('news').checked = cfg.news;
  render();
}

function buildWheel() {
  wheel.querySelectorAll('.station').forEach(b => b.remove());
  const items = [{ id: null, name: 'Radio Off' }, ...stations];
  const n = items.length, R = 42;
  const size = Math.min(15, 0.88 * (2 * Math.PI * R) / n);
  wheel.style.setProperty('--btn', size + '%');
  items.forEach((s, i) => {
    const a = (i / n) * Math.PI * 2 - Math.PI / 2;
    const b = document.createElement('button');
    b.className = 'station' + (s.id ? '' : ' off-btn');
    b.style.left = (50 + R * Math.cos(a)) + '%';
    b.style.top = (50 + R * Math.sin(a)) + '%';
    const sp = document.createElement('span'); sp.textContent = s.id ? shortName(s.name) : 'OFF'; b.appendChild(sp);
    b.setAttribute('aria-label', s.name);
    b.onclick = () => tune(s.id);
    wheel.appendChild(b);
    els[s.id ?? 'off'] = b;
  });
  fitLabels();
}

// Shrink each label until it fits inside its circle.
function fitLabels() {
  for (const b of wheel.querySelectorAll('.station')) {
    const sp = b.querySelector('span');
    let size = Math.min(16, b.clientWidth / 2.6);
    do { sp.style.fontSize = size + 'px'; size -= 0.5; } while (size >= 6 && sp.scrollWidth > b.clientWidth - 6);
  }
}
window.addEventListener('resize', fitLabels);

async function startAudio() {
  try { if (navigator.audioSession) navigator.audioSession.type = 'playback'; } catch (e) {}
  ctx = new (window.AudioContext || window.webkitAudioContext)({ sampleRate: 48000 });
  await ctx.audioWorklet.addModule('worklet.js');
  node = new AudioWorkletNode(ctx, 'pcm-player', { numberOfInputs: 0, outputChannelCount: [2] });
  node.connect(ctx.destination);
  if (ctx.state !== 'running') await ctx.resume();
  // iOS suspends audio on interruptions (calls, Siri); pick back up when the page is shown again.
  document.addEventListener('visibilitychange', () => { if (!document.hidden && ctx.state !== 'running') ctx.resume(); });
  setupMediaSession();
}

function tune(id) {
  if (!node) return;
  const my = ++gen;
  node.port.postMessage({ type: 'flush', gen: my });
  current = id;
  if (id) lastOn = id;
  render();
  if (id) pump(my, id);
}

const sleep = ms => new Promise(r => setTimeout(r, ms));

// Renders the station ahead of real time and feeds the audio thread.
async function pump(my, id) {
  let t = Clock.now;
  const r = runners[id];
  while (my === gen) {
    const now = Clock.now;
    if (t < now - RATE) t = now;          // the page was paused for a while; rejoin the station live
    if (t - now > LEAD) { await sleep(500); continue; }
    let pcm;
    try { pcm = await mixer.render(r, t, BLOCK); }
    catch (e) { console.error(e); await sleep(1000); continue; }
    if (my !== gen) break;
    node.port.postMessage({ type: 'pcm', data: pcm, gen: my }, [pcm.buffer]);
    t += BLOCK;
  }
}

function step(dir) {
  const ids = [null, ...stations.map(s => s.id)];
  const i = ids.indexOf(current);
  tune(ids[(i + dir + ids.length) % ids.length]);
}

let lastMeta = '';
function render() {
  for (const [k, b] of Object.entries(els)) b.classList.toggle('on', (current ?? 'off') === k);
  const s = stations.find(x => x.id === current);
  const name = $('st-name'), t = $('np-title'), a = $('np-artist'), tag = $('np-tag');
  if (!s) { name.textContent = 'Radio Off'; t.textContent = ''; a.textContent = ''; tag.classList.remove('show'); return; }
  const np = nowPlaying(runners[s.id], Clock.now);
  name.textContent = s.name;
  t.textContent = np.title || (s.kind === 'mix' ? 'Live mix' : '');
  a.textContent = np.artist || '';
  tag.textContent = np.dj ? 'DJ on air' : np.kind === 'ad' ? 'Advert' : np.kind === 'news' ? 'News' : '';
  tag.classList.toggle('show', !!(np.dj || np.kind === 'ad' || np.kind === 'news'));
  const meta = s.name + '|' + t.textContent + '|' + a.textContent;
  if (meta !== lastMeta && 'mediaSession' in navigator && window.MediaMetadata) {
    lastMeta = meta;
    navigator.mediaSession.metadata = new MediaMetadata({ title: t.textContent || s.name, artist: a.textContent || s.name, album: s.name, artwork: [{ src: 'icon-512.png', sizes: '512x512', type: 'image/png' }] });
  }
}
setInterval(() => { if (!$('radio').hidden) render(); }, 1000);

function setupMediaSession() {
  if (!('mediaSession' in navigator)) return;
  const ms = navigator.mediaSession;
  const h = (a, f) => { try { ms.setActionHandler(a, f); } catch (e) {} };
  h('nexttrack', () => step(1));
  h('previoustrack', () => step(-1));
  h('pause', () => tune(null));
  h('play', () => tune(lastOn || stations[0].id));
}

$('start-btn').onclick = async () => {
  $('start').hidden = true;
  await startAudio();
  tune(stations[Math.floor(Math.random() * stations.length)].id);
};
$('prev').onclick = () => step(-1);
$('next').onclick = () => step(1);
$('power').onclick = () => tune(current ? null : (lastOn || stations[0].id));
$('news').onchange = (e) => { cfg.news = e.target.checked; try { localStorage.setItem('news', cfg.news ? '1' : '0'); } catch (err) {} };
$('replace').onclick = async () => {
  if (!confirm('Remove the radio from this app so you can copy in a new file?')) return;
  tune(null); mixer.clear(); await Store.clear(); showSetup();
};
document.addEventListener('keydown', (e) => {
  if (!node) return;
  if (e.key === 'ArrowRight') step(1); else if (e.key === 'ArrowLeft') step(-1);
});
boot();
