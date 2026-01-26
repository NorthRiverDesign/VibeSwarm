// VibeSwarm Service Worker
// Increment CACHE_VERSION when icons or critical assets change to force cache refresh
const CACHE_VERSION = 3;
const CACHE_NAME = `vibeswarm-v${CACHE_VERSION}`;
const OFFLINE_URL = '/offline.html';

// Static assets to cache for offline use
// Add ?v=VERSION query param to icons to bust browser cache
const STATIC_ASSETS = [
    '/',
    '/offline.html',
    '/css/site.css',
    '/lib/bootstrap/css/bootstrap.min.css',
    '/lib/bootstrap/js/bootstrap.bundle.min.js',
    `/favicon.svg?v=${CACHE_VERSION}`,
    `/favicon-96x96.png?v=${CACHE_VERSION}`,
    `/apple-touch-icon.png?v=${CACHE_VERSION}`,
    `/apple-touch-icon-120x120.png?v=${CACHE_VERSION}`,
    `/apple-touch-icon-152x152.png?v=${CACHE_VERSION}`,
    `/apple-touch-icon-167x167.png?v=${CACHE_VERSION}`,
    `/apple-touch-icon-180x180.png?v=${CACHE_VERSION}`,
    `/web-app-manifest-192x192.png?v=${CACHE_VERSION}`,
    `/web-app-manifest-512x512.png?v=${CACHE_VERSION}`,
    '/manifest.json'
];

// Install event - cache static assets
self.addEventListener('install', (event) => {
    event.waitUntil(
        caches.open(CACHE_NAME)
            .then((cache) => {
                console.log('[ServiceWorker] Caching static assets');
                return cache.addAll(STATIC_ASSETS);
            })
            .then(() => {
                // Force the waiting service worker to become active
                return self.skipWaiting();
            })
    );
});

// Activate event - clean up old caches
self.addEventListener('activate', (event) => {
    event.waitUntil(
        caches.keys()
            .then((cacheNames) => {
                return Promise.all(
                    cacheNames
                        .filter((cacheName) => cacheName !== CACHE_NAME)
                        .map((cacheName) => caches.delete(cacheName))
                );
            })
            .then(() => {
                // Take control of all pages immediately
                return self.clients.claim();
            })
    );
});

// Fetch event - network first, fallback to cache
self.addEventListener('fetch', (event) => {
    const request = event.request;

    // Skip non-GET requests
    if (request.method !== 'GET') {
        return;
    }

    // Skip SignalR and Blazor framework requests - these must go to network
    if (request.url.includes('_blazor') ||
        request.url.includes('_framework') ||
        request.url.includes('negotiate')) {
        return;
    }

    event.respondWith(
        fetch(request)
            .then((response) => {
                // Clone the response for caching
                const responseClone = response.clone();

                // Cache successful responses for static assets
                if (response.ok && isStaticAsset(request.url)) {
                    caches.open(CACHE_NAME)
                        .then((cache) => {
                            cache.put(request, responseClone);
                        });
                }

                return response;
            })
            .catch(() => {
                // Network failed, try cache
                return caches.match(request)
                    .then((cachedResponse) => {
                        if (cachedResponse) {
                            return cachedResponse;
                        }

                        // For navigation requests, show offline page
                        if (request.mode === 'navigate') {
                            return caches.match(OFFLINE_URL);
                        }

                        return new Response('Network error', {
                            status: 408,
                            headers: { 'Content-Type': 'text/plain' }
                        });
                    });
            })
    );
});

// Helper to determine if URL is a static asset worth caching
function isStaticAsset(url) {
    const staticExtensions = ['.css', '.js', '.png', '.jpg', '.jpeg', '.svg', '.ico', '.woff', '.woff2'];
    return staticExtensions.some(ext => url.toLowerCase().includes(ext));
}
