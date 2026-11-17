// Push notification sign-up for this device, used by the Settings page (PushNotificationsSection).
// The service worker (service-worker.js) shows the notifications. Every export resolves to plain
// data, so a browser without push support reads as unsupported instead of throwing.

const isAppleMobile = () =>
	/iPad|iPhone|iPod/.test(navigator.userAgent) ||
	(navigator.platform === "MacIntel" && navigator.maxTouchPoints > 1);

const isStandalone = () =>
	window.matchMedia?.("(display-mode: standalone)").matches === true ||
	window.navigator.standalone === true;

function describeDevice() {
	const ua = navigator.userAgent;
	if (/iPhone|iPod/.test(ua)) return "iPhone";
	if (isAppleMobile()) return "iPad";
	const device = /Android/.test(ua) ? "Android"
		: /Mac OS X/.test(ua) ? "Mac"
		: /Windows/.test(ua) ? "Windows"
		: /Linux/.test(ua) ? "Linux"
		: "browser";
	const browser = /Edg\//.test(ua) ? "Edge"
		: /Firefox\//.test(ua) ? "Firefox"
		: /Chrome\//.test(ua) ? "Chrome"
		: /Safari\//.test(ua) ? "Safari"
		: "";
	return browser ? `${browser} on ${device}` : device;
}

function toInfo(subscription) {
	if (!subscription) return null;
	const json = subscription.toJSON();
	return { endpoint: json.endpoint, p256dh: json.keys?.p256dh ?? "", auth: json.keys?.auth ?? "" };
}

function base64UrlToBytes(value) {
	const base64 = value.replace(/-/g, "+").replace(/_/g, "/").padEnd(Math.ceil(value.length / 4) * 4, "=");
	return Uint8Array.from(atob(base64), (c) => c.charCodeAt(0));
}

function sameKey(buffer, publicKey) {
	if (!buffer) return false;
	const a = new Uint8Array(buffer);
	const b = base64UrlToBytes(publicKey);
	return a.length === b.length && a.every((byte, i) => byte === b[i]);
}

const isSupported = () => "serviceWorker" in navigator && "PushManager" in window && "Notification" in window;

export async function getState() {
	const state = {
		https: window.location.protocol === "https:",
		supported: isSupported(),
		needsHomeScreen: isAppleMobile() && !isStandalone(),
		permission: "Notification" in window ? Notification.permission : "unsupported",
		hasWorker: false,
		updateWaiting: false,
		subscription: null,
		deviceName: describeDevice(),
	};
	if (!state.supported) return state;

	try {
		const registration = await navigator.serviceWorker.getRegistration("/");
		if (!registration) return state;
		state.hasWorker = true;
		// The push handlers arrive with a new worker, which waits until the update banner is accepted.
		state.updateWaiting = !!registration.waiting;
		state.subscription = toInfo(await registration.pushManager.getSubscription());
	} catch (error) {
		console.warn("[Push] Could not read the subscription", error);
	}
	return state;
}

// Call straight from the tap: iOS only asks for permission during a user gesture.
export async function subscribe(publicKey) {
	try {
		const permission = await Notification.requestPermission();
		if (permission !== "granted") {
			return { error: permission === "denied" ? "denied" : "dismissed" };
		}

		const registration = await navigator.serviceWorker.ready;
		let subscription = await registration.pushManager.getSubscription();
		if (subscription && !sameKey(subscription.options?.applicationServerKey, publicKey)) {
			await subscription.unsubscribe();
			subscription = null;
		}
		subscription ??= await registration.pushManager.subscribe({
			userVisibleOnly: true,
			applicationServerKey: base64UrlToBytes(publicKey),
		});
		return { subscription: toInfo(subscription) };
	} catch (error) {
		return { error: String(error?.message ?? error) };
	}
}

// Returns the endpoint that was dropped, or null when there was none.
export async function unsubscribe() {
	try {
		const registration = await navigator.serviceWorker.getRegistration("/");
		const subscription = await registration?.pushManager.getSubscription();
		if (!subscription) return null;
		const endpoint = subscription.endpoint;
		await subscription.unsubscribe();
		return endpoint;
	} catch (error) {
		console.warn("[Push] Could not unsubscribe", error);
		return null;
	}
}
