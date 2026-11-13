// In development, always fetch from the network and do not enable offline support.
// This is because caching would make development more difficult (changes would not
// be reflected on the first load after each change).
//
// Take over at once and drop what a published build cached on this origin: if one was ever
// served here, its cache-first worker keeps controlling the page (a reload does not release
// it) and goes on serving the old app while this worker sits waiting.
self.addEventListener('install', () => self.skipWaiting());
self.addEventListener('activate', event => event.waitUntil((async () => {
    const keys = await caches.keys();
    await Promise.all(keys.filter(key => key.startsWith('offline-cache-')).map(key => caches.delete(key)));
    await self.clients.claim();
})()));
self.addEventListener('fetch', () => { });