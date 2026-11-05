// LocalStream service worker: lets the web page be installed as an app. Everything comes from the
// PC live (media is never cached here); only the page itself is kept so the app opens instantly
// and shows a friendly message when the PC is off.
'use strict';
const SHELL = 'localstream-shell-v3';
const OFFLINE = 'localstream-offline'; // files saved with "Save for offline" (kept until removed)
const FILES = ['/', '/app.js', '/library.js', '/app.css', '/icon-192.png'];

self.addEventListener('install', (e) => {
  e.waitUntil(caches.open(SHELL).then(c => c.addAll(FILES).catch(() => {})).then(() => self.skipWaiting()));
});

self.addEventListener('activate', (e) => {
  e.waitUntil(caches.keys().then(keys => Promise.all(keys.filter(k => k !== SHELL && k !== OFFLINE).map(k => caches.delete(k)))).then(() => self.clients.claim()));
});

self.addEventListener('fetch', (e) => {
  const url = new URL(e.request.url);
  if (e.request.method !== 'GET' || url.origin !== location.origin) return;

  // Saved media and covers: from the offline store when saved there (with seeking), else the network.
  if (url.pathname.startsWith('/file/') || url.pathname.startsWith('/thumb/')) {
    e.respondWith((async () => {
      const cache = await caches.open(OFFLINE);
      const saved = await cache.match(url.pathname);
      if (!saved) return fetch(e.request);
      return rangeOf(e.request, saved);
    })());
    return;
  }

  // The page itself (any ?view=…): network first, the saved copy when the PC can't be reached.
  const isPage = e.request.mode === 'navigate' && url.pathname === '/';
  if (!isPage && (!FILES.includes(url.pathname) || url.search)) return; // API: always live
  e.respondWith(fetch(e.request).then(res => {
    if (res.ok && !url.search) { const copy = res.clone(); caches.open(SHELL).then(c => c.put(e.request, copy)); }
    return res;
  }).catch(() => caches.match(isPage ? '/' : e.request)));
});

/** Players ask for byte ranges; answer them from the saved file. */
async function rangeOf(request, saved) {
  const range = request.headers.get('range');
  if (!range) return saved;
  const blob = await saved.blob();
  const m = /bytes=(\d*)-(\d*)/.exec(range) || [];
  let start = m[1] ? +m[1] : 0, end = m[2] ? +m[2] : blob.size - 1;
  if (!m[1] && m[2]) { start = Math.max(0, blob.size - +m[2]); end = blob.size - 1; }
  end = Math.min(end, blob.size - 1);
  if (start > end) return new Response(null, { status: 416, headers: { 'Content-Range': `bytes */${blob.size}` } });
  return new Response(blob.slice(start, end + 1), {
    status: 206,
    headers: {
      'Content-Type': saved.headers.get('Content-Type') || 'application/octet-stream',
      'Content-Range': `bytes ${start}-${end}/${blob.size}`,
      'Content-Length': String(end - start + 1),
      'Accept-Ranges': 'bytes',
    },
  });
}
