// ParkOL Service Worker – macht die App offline-fähig.
// Strategie: App-Dateien "stale-while-revalidate", live.json immer zuerst aus
// dem Netz. Kartenkacheln und Suchanfragen (fremde Server) werden nicht angefasst.
const CACHE = 'parkol-v1';
const SHELL = ['./', './index.html', './manifest.webmanifest', './icons/favicon.svg', './icons/icon-192.png'];

self.addEventListener('install', (e) => {
  e.waitUntil(caches.open(CACHE).then((c) => c.addAll(SHELL)).then(() => self.skipWaiting()));
});

self.addEventListener('activate', (e) => {
  e.waitUntil(
    caches.keys().then((keys) => Promise.all(keys.filter((k) => k !== CACHE).map((k) => caches.delete(k)))).then(() => self.clients.claim()),
  );
});

self.addEventListener('fetch', (e) => {
  const url = new URL(e.request.url);
  if (e.request.method !== 'GET' || url.origin !== self.location.origin) return;
  if (url.pathname.endsWith('/live.json')) {
    e.respondWith(fetch(e.request).catch(() => caches.match(e.request, { ignoreSearch: true })));
    return;
  }
  e.respondWith(
    caches.open(CACHE).then(async (cache) => {
      const cached = await cache.match(e.request);
      const network = fetch(e.request)
        .then((res) => {
          if (res.ok) cache.put(e.request, res.clone());
          return res;
        })
        .catch(() => cached);
      return cached || network;
    }),
  );
});
