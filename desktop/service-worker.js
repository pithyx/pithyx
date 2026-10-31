// The desktop's service worker (D380), registered only once the device trusts the box. It keeps the content-hashed
// files under /assets/ of the current build, so a reload needs no request for them. Everything else (the page, the API,
// the WebSocket, app frontends) goes to the network as if there were no worker. The build replaces the cache name with
// one per build; a new build's worker drops the old cache.
const CACHE = '__PITHYX_ASSET_CACHE__';

self.addEventListener('install', () => {
  void self.skipWaiting();
});

self.addEventListener('activate', (event) => {
  event.waitUntil(
    (async () => {
      for (const name of await caches.keys()) {
        if (name !== CACHE) {
          await caches.delete(name);
        }
      }
      await self.clients.claim();
    })(),
  );
});

self.addEventListener('fetch', (event) => {
  const request = event.request;
  const url = new URL(request.url);
  if (
    request.method !== 'GET' ||
    url.origin !== self.location.origin ||
    !url.pathname.startsWith('/assets/')
  ) {
    return;
  }
  event.respondWith(
    (async () => {
      const cache = await caches.open(CACHE);
      const cached = await cache.match(request);
      if (cached !== undefined) {
        return cached;
      }
      const response = await fetch(request);
      if (response.ok) {
        await cache.put(request, response.clone());
      }
      return response;
    })(),
  );
});
