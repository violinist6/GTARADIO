// Keeps the app itself available offline. The radio audio lives in the app's own storage, not here.
const CACHE = 'gtaradio-v1';
const FILES = ['./', 'index.html', 'style.css', 'app.js', 'engine.js', 'mixer.js', 'store.js', 'import-worker.js', 'worklet.js', 'opus-decoder.min.js', 'manifest.webmanifest', 'icon-180.png', 'icon-512.png'];
self.addEventListener('install', e => e.waitUntil(caches.open(CACHE).then(c => c.addAll(FILES)).then(() => self.skipWaiting())));
self.addEventListener('activate', e => e.waitUntil(caches.keys().then(ks => Promise.all(ks.filter(k => k !== CACHE).map(k => caches.delete(k)))).then(() => self.clients.claim())));
// Network first so updates arrive when online; the cache answers when there's no signal.
self.addEventListener('fetch', e => {
  if (e.request.method !== 'GET' || new URL(e.request.url).origin !== location.origin) return;
  e.respondWith(fetch(e.request).then(r => { const copy = r.clone(); caches.open(CACHE).then(c => c.put(e.request, copy)); return r; })
    .catch(() => caches.match(e.request, { ignoreSearch: true })));
});
