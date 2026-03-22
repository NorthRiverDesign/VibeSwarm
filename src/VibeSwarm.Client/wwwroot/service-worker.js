// VibeSwarm Service Worker
// Increment CACHE_VERSION when icons or critical assets change to force cache refresh
const CACHE_VERSION = 8;
const CACHE_NAME = `vibeswarm-v${CACHE_VERSION}`;
const OFFLINE_URL = "/offline.html";

// Static assets to cache for offline use
// Add ?v=VERSION query param to icons to bust browser cache
const STATIC_ASSETS = [
	"/",
	"/offline.html",
	"/css/site.css",
	"/lib/bootstrap/css/bootstrap.min.css",
	"/lib/bootstrap/js/bootstrap.bundle.min.js",
	`/favicon.svg?v=${CACHE_VERSION}`,
	`/favicon-96x96.png?v=${CACHE_VERSION}`,
	`/apple-touch-icon.png?v=${CACHE_VERSION}`,
	`/apple-touch-icon-120x120.png?v=${CACHE_VERSION}`,
	`/apple-touch-icon-152x152.png?v=${CACHE_VERSION}`,
	`/apple-touch-icon-167x167.png?v=${CACHE_VERSION}`,
	`/apple-touch-icon-180x180.png?v=${CACHE_VERSION}`,
	`/web-app-manifest-192x192.png?v=${CACHE_VERSION}`,
	`/web-app-manifest-512x512.png?v=${CACHE_VERSION}`,
	"/manifest.json",
];

// Install event - cache static assets
// NOTE: skipWaiting() is intentionally NOT called here. The new service worker
// waits in the "installed" state until the page sends a SKIP_WAITING message.
// This prevents mid-session takeovers that can disrupt running jobs.
self.addEventListener("install", (event) => {
	event.waitUntil(
		caches
			.open(CACHE_NAME)
			.then((cache) => {
				console.log("[ServiceWorker] Caching static assets");
				return cache.addAll(STATIC_ASSETS);
			})
			.then(() => {
				// Notify all open clients that an update is waiting
				return self.clients.matchAll({ type: "window" }).then((clients) => {
					clients.forEach((client) =>
						client.postMessage({ type: "SW_UPDATE_WAITING" }),
					);
				});
			}),
	);
});

// Listen for SKIP_WAITING message from the page (sent when user approves reload)
self.addEventListener("message", (event) => {
	if (event.data && event.data.type === "SKIP_WAITING") {
		self.skipWaiting();
	}
});

// Activate event - clean up old caches
self.addEventListener("activate", (event) => {
	event.waitUntil(
		caches
			.keys()
			.then((cacheNames) => {
				return Promise.all(
					cacheNames
						.filter((cacheName) => cacheName !== CACHE_NAME)
						.map((cacheName) => caches.delete(cacheName)),
				);
			})
			.then(() => {
				// Notify all clients the update has been applied
				return self.clients.matchAll({ type: "window" }).then((clients) => {
					clients.forEach((client) =>
						client.postMessage({ type: "SW_ACTIVATED" }),
					);
				});
			}),
	);
});

// Fetch event - network first, fallback to cache
self.addEventListener("fetch", (event) => {
	const request = event.request;

	// Skip non-GET requests
	if (request.method !== "GET") {
		return;
	}

	// iOS Safari sends range requests for media - handle them specially
	// Range requests can't be satisfied from cache easily, so always go to network
	if (request.headers.get("range")) {
		return;
	}

	// Skip API, SignalR, and Blazor framework requests - these must go to network
	// This is critical for authentication to work correctly on iOS Safari
	if (
		request.url.includes("/api/") ||
		request.url.includes("_blazor") ||
		request.url.includes("_framework") ||
		request.url.includes("negotiate") ||
		request.url.includes("/login") ||
		request.url.includes("/logout") ||
		request.url.includes("/account/")
	) {
		return;
	}

	event.respondWith(
		fetch(request)
			.then((response) => {
				// Clone the response for caching
				const responseClone = response.clone();

				// Cache successful responses for static assets
				if (response.ok && isStaticAsset(request.url)) {
					caches.open(CACHE_NAME).then((cache) => {
						cache.put(request, responseClone);
					});
				}

				return response;
			})
			.catch(() => {
				// Network failed, try cache
				return caches.match(request).then((cachedResponse) => {
					if (cachedResponse) {
						return cachedResponse;
					}

					// For navigation requests, show offline page
					if (request.mode === "navigate") {
						return caches.match(OFFLINE_URL);
					}

					return new Response("Network error", {
						status: 408,
						headers: { "Content-Type": "text/plain" },
					});
				});
			}),
	);
});

// Helper to determine if URL is a static asset worth caching
function isStaticAsset(url) {
	const staticExtensions = [
		".css",
		".js",
		".png",
		".jpg",
		".jpeg",
		".svg",
		".ico",
		".woff",
		".woff2",
	];
	return staticExtensions.some((ext) => url.toLowerCase().includes(ext));
}
