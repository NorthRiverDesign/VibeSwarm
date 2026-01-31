// Blazor Server PWA Reconnection Handler
// Enhanced for iOS PWA with stateful reconnect support
// Handles reconnection when the app returns from background on iOS/mobile devices
(function () {
	"use strict";

	const ReconnectHandler = {
		// Configuration
		maxReconnectAttempts: 25,
		reconnectAttempts: 0,
		reconnectInterval: null,
		isReconnecting: false,
		lastVisibilityChange: Date.now(),
		connectionLostTime: null,
		lastSuccessfulPing: Date.now(),

		// iOS-specific detection
		isIOS: /iPad|iPhone|iPod/.test(navigator.userAgent) && !window.MSStream,
		isStandalone:
			window.navigator.standalone === true ||
			window.matchMedia("(display-mode: standalone)").matches,

		// Thresholds (adjusted for iOS behavior)
		get backgroundThreshold() {
			// iOS can take 10-30 seconds to resume WebSocket
			return this.isIOSPWA ? 15000 : 30000;
		},

		get extendedBackgroundThreshold() {
			// After this long, show full modal instead of toast
			return this.isIOSPWA ? 120000 : 180000; // 2 or 3 minutes
		},

		get isIOSPWA() {
			return this.isIOS && this.isStandalone;
		},

		init() {
			this.setupVisibilityHandler();
			this.setupBlazorReconnectHandler();
			this.setupHeartbeat();
			this.setupNetworkHandlers();
			this.setupFreezeHandler();

			// Store reference in global state
			if (window.VibeSwarmReconnect) {
				window.VibeSwarmReconnect.handler = this;
			}

			console.log(
				`[ReconnectHandler] Initialized (iOS: ${this.isIOS}, PWA: ${this.isStandalone}, iOS PWA: ${this.isIOSPWA})`,
			);
		},

		// Handle page visibility changes (app going to/from background)
		setupVisibilityHandler() {
			document.addEventListener("visibilitychange", () => {
				if (document.visibilityState === "visible") {
					const timeSinceHidden = Date.now() - this.lastVisibilityChange;
					console.log(
						`[ReconnectHandler] App became visible after ${Math.round(timeSinceHidden / 1000)}s`,
					);

					// iOS PWA: Always check connection when becoming visible
					if (this.isIOSPWA || timeSinceHidden > this.backgroundThreshold) {
						// Use toast for short backgrounds, modal for extended
						const useModal = timeSinceHidden > this.extendedBackgroundThreshold;
						this.checkAndReconnect(useModal);
					}
				} else {
					this.lastVisibilityChange = Date.now();
					console.log("[ReconnectHandler] App going to background");

					// Record state before going to background
					if (window.VibeSwarmReconnect) {
						window.VibeSwarmReconnect.lastHiddenTime =
							this.lastVisibilityChange;
					}
				}
			});

			// iOS-specific: handle pageshow event for PWA (bfcache restore)
			window.addEventListener("pageshow", (event) => {
				if (event.persisted) {
					console.log("[ReconnectHandler] Page restored from bfcache");
					// bfcache restore always needs reconnection check
					setTimeout(() => this.checkAndReconnect(true), 500);
				}
			});

			// Handle focus events (additional trigger for reconnection)
			window.addEventListener("focus", () => {
				const timeSinceHidden = Date.now() - this.lastVisibilityChange;
				if (timeSinceHidden > 60000) {
					console.log(
						"[ReconnectHandler] Window focused after extended period",
					);
					this.checkAndReconnect(false);
				}
			});

			// iOS Safari resume event (fired when Safari becomes active)
			if (this.isIOS) {
				window.addEventListener("resume", () => {
					console.log("[ReconnectHandler] iOS resume event");
					setTimeout(() => this.checkAndReconnect(false), 1000);
				});
			}
		},

		// Handle network online/offline events
		setupNetworkHandlers() {
			window.addEventListener("online", () => {
				console.log("[ReconnectHandler] Network came online");
				// Wait a moment for network to stabilize
				setTimeout(() => this.checkAndReconnect(false), 2000);
			});

			window.addEventListener("offline", () => {
				console.log("[ReconnectHandler] Network went offline");
				// Don't show reconnect UI immediately - wait for it to come back
			});
		},

		// Handle page freeze (iOS background suspension)
		setupFreezeHandler() {
			// Chrome/Safari freeze event (page being suspended)
			document.addEventListener("freeze", () => {
				console.log("[ReconnectHandler] Page freezing");
				this.lastVisibilityChange = Date.now();
			});

			// Resume from freeze
			document.addEventListener("resume", () => {
				console.log("[ReconnectHandler] Page resumed from freeze");
				setTimeout(() => this.checkAndReconnect(false), 1000);
			});
		},

		// Override Blazor's default reconnection behavior
		setupBlazorReconnectHandler() {
			// Listen for custom Blazor connection events (dispatched from _Host.cshtml)
			window.addEventListener("blazor-connection-down", (event) => {
				console.log("[ReconnectHandler] Blazor connection down event received");
				this.connectionLostTime = Date.now();
				this.handleConnectionDown(event.detail?.error);
			});

			window.addEventListener("blazor-connection-up", () => {
				console.log("[ReconnectHandler] Blazor connection up event received");
				this.handleConnectionUp();
			});

			// Also hook into Blazor's default reconnection handler if available
			this.waitForBlazor(() => {
				this.configureBlazorReconnect();
			});
		},

		waitForBlazor(callback) {
			if (window.Blazor) {
				callback();
				return;
			}

			const waitInterval = setInterval(() => {
				if (window.Blazor) {
					clearInterval(waitInterval);
					callback();
				}
			}, 100);

			// Timeout after 15 seconds
			setTimeout(() => clearInterval(waitInterval), 15000);
		},

		configureBlazorReconnect() {
			// Override the default reconnection handler if it exists
			if (Blazor.defaultReconnectionHandler) {
				const originalOnConnectionDown =
					Blazor.defaultReconnectionHandler.onConnectionDown;
				const originalOnConnectionUp =
					Blazor.defaultReconnectionHandler.onConnectionUp;

				Blazor.defaultReconnectionHandler.onConnectionDown = (
					options,
					error,
				) => {
					this.connectionLostTime = Date.now();
					this.handleConnectionDown(error);
					if (originalOnConnectionDown) {
						originalOnConnectionDown.call(
							Blazor.defaultReconnectionHandler,
							options,
							error,
						);
					}
				};

				Blazor.defaultReconnectionHandler.onConnectionUp = () => {
					this.handleConnectionUp();
					if (originalOnConnectionUp) {
						originalOnConnectionUp.call(Blazor.defaultReconnectionHandler);
					}
				};
			}

			console.log("[ReconnectHandler] Blazor reconnection handler configured");
		},

		// Handle connection going down
		handleConnectionDown(error) {
			console.log(
				"[ReconnectHandler] Connection down:",
				error?.message || "Unknown",
			);

			// Determine if we should show toast or modal
			const timeSinceHidden = Date.now() - this.lastVisibilityChange;
			const isExtendedBackground =
				timeSinceHidden > this.extendedBackgroundThreshold;

			if (isExtendedBackground) {
				this.showReconnectModal();
			} else {
				this.showReconnectToast();
			}

			this.startReconnectAttempts();
		},

		// Handle connection restored
		handleConnectionUp() {
			console.log("[ReconnectHandler] Connection restored");
			this.connectionLostTime = null;
			this.reconnectAttempts = 0;
			this.isReconnecting = false;
			this.lastSuccessfulPing = Date.now();

			this.hideReconnectToast();
			this.hideReconnectModal();
			this.clearReconnectInterval();

			// Reinitialize the global SignalR hub
			this.reinitializeSignalR();

			// Notify the app to refresh any stale data
			this.notifyAppReconnected();
		},

		// Check if connection is alive and reconnect if needed
		async checkAndReconnect(useModal = false) {
			if (this.isReconnecting) {
				console.log("[ReconnectHandler] Already reconnecting, skipping");
				return;
			}

			// Check if Blazor circuit is still alive
			try {
				const isConnected = await this.isBlazorConnected();
				if (!isConnected) {
					console.log(
						"[ReconnectHandler] Blazor not connected, initiating reconnect",
					);

					if (useModal) {
						this.showReconnectModal();
					} else {
						this.showReconnectToast();
					}

					this.startReconnectAttempts();
				} else {
					console.log("[ReconnectHandler] Blazor connection is healthy");
					// Also reinitialize SignalR hub in case it disconnected
					this.reinitializeSignalR();
				}
			} catch (error) {
				console.error("[ReconnectHandler] Error checking connection:", error);
				if (useModal) {
					this.showReconnectModal();
				} else {
					this.showReconnectToast();
				}
				this.startReconnectAttempts();
			}
		},

		// Check Blazor connection status
		isBlazorConnected() {
			return new Promise((resolve) => {
				// Check the reconnect state flag
				if (window.VibeSwarmReconnect?.isReconnecting) {
					resolve(false);
					return;
				}

				// Try to check internal Blazor state
				if (
					window.Blazor &&
					window.Blazor._internal &&
					window.Blazor._internal.navigationManager
				) {
					try {
						// Circuit is likely alive if we can access internal state
						resolve(true);
					} catch {
						resolve(false);
					}
				} else {
					// Check if the reconnect modal is showing (Blazor's indication)
					const modal = document.getElementById("components-reconnect-modal");
					if (modal && modal.classList.contains("components-reconnect-show")) {
						resolve(false);
					} else {
						resolve(true);
					}
				}
			});
		},

		// Start reconnection attempts
		startReconnectAttempts() {
			if (this.reconnectInterval) {
				return; // Already attempting
			}

			this.isReconnecting = true;
			this.reconnectAttempts = 0;

			// iOS needs more aggressive initial retries
			const baseInterval = this.isIOSPWA ? 2000 : 3000;

			this.reconnectInterval = setInterval(
				() => {
					this.reconnectAttempts++;

					console.log(
						`[ReconnectHandler] Reconnect attempt ${this.reconnectAttempts}/${this.maxReconnectAttempts}`,
					);

					// Update toast/modal message
					this.updateReconnectMessage(`Attempt ${this.reconnectAttempts}...`);

					if (this.reconnectAttempts >= this.maxReconnectAttempts) {
						this.handleReconnectFailure();
					} else {
						// Try to reconnect by triggering Blazor's internal reconnect
						this.triggerBlazorReconnect();
					}
				},
				this.isIOSPWA ? 2500 : 3000,
			);
		},

		// Trigger Blazor's reconnection mechanism
		triggerBlazorReconnect() {
			try {
				// Try Blazor's reconnect method first
				if (window.Blazor && window.Blazor.reconnect) {
					window.Blazor.reconnect().catch(() => {
						// Reconnect failed, will retry
					});
					return;
				}

				// Fallback: Try to find and click the reconnect button if it exists
				const reconnectButton = document.querySelector(
					"[data-blazor-reconnect-click]",
				);
				if (reconnectButton) {
					reconnectButton.click();
				}
			} catch (error) {
				console.error("[ReconnectHandler] Error triggering reconnect:", error);
			}
		},

		// Handle when all reconnect attempts fail
		handleReconnectFailure() {
			this.clearReconnectInterval();
			this.isReconnecting = false;

			console.log(
				"[ReconnectHandler] Max reconnect attempts reached, showing reload option",
			);

			// Switch to modal if we were showing toast
			this.hideReconnectToast();
			this.showReconnectModal();

			this.updateReconnectMessage("Connection lost");
			this.showReloadButton();
		},

		// Clear reconnection interval
		clearReconnectInterval() {
			if (this.reconnectInterval) {
				clearInterval(this.reconnectInterval);
				this.reconnectInterval = null;
			}
		},

		// === Toast UI (non-intrusive) ===

		showReconnectToast() {
			const toast = document.getElementById("reconnect-toast");
			if (toast) {
				toast.classList.add("reconnect-toast-show");
				// Start progress animation
				const progress = toast.querySelector(".reconnect-toast-progress");
				if (progress) {
					progress.style.animation = "reconnect-progress 30s linear";
				}
			}
		},

		hideReconnectToast() {
			const toast = document.getElementById("reconnect-toast");
			if (toast) {
				toast.classList.remove("reconnect-toast-show");
				const progress = toast.querySelector(".reconnect-toast-progress");
				if (progress) {
					progress.style.animation = "";
				}
			}
		},

		updateToastMessage(message) {
			const messageEl = document.querySelector(".reconnect-toast-message");
			if (messageEl) {
				messageEl.textContent = message;
			}
		},

		// === Modal UI (for extended disconnections) ===

		showReconnectModal() {
			const modal = document.getElementById("components-reconnect-modal");
			if (modal) {
				modal.classList.add("components-reconnect-show");
				this.hideReloadButton();
			}
		},

		hideReconnectModal() {
			const modal = document.getElementById("components-reconnect-modal");
			if (modal) {
				modal.classList.remove("components-reconnect-show");
			}
		},

		updateReconnectMessage(message) {
			// Update both toast and modal
			this.updateToastMessage(message);

			const messageEl = document.querySelector(".reconnect-message");
			if (messageEl) {
				messageEl.textContent = message;
			}
		},

		showReloadButton() {
			const button = document.getElementById("reconnect-reload-btn");
			if (button) {
				button.classList.remove("d-none");
			}
			// Hide the spinner
			const spinner = document.querySelector(".reconnect-spinner");
			if (spinner) {
				spinner.style.display = "none";
			}
		},

		hideReloadButton() {
			const button = document.getElementById("reconnect-reload-btn");
			if (button) {
				button.classList.add("d-none");
			}
			// Show the spinner
			const spinner = document.querySelector(".reconnect-spinner");
			if (spinner) {
				spinner.style.display = "block";
			}
		},

		// Reinitialize the SignalR hub after reconnection
		reinitializeSignalR() {
			if (window.VibeSwarmHub) {
				// Force reinitialize if connection was lost
				if (!window.VibeSwarmHub.isConnected) {
					console.log("[ReconnectHandler] Reinitializing SignalR hub");
					window.VibeSwarmHub.isConnecting = false;
					window.VibeSwarmHub.isConnected = false;
					window.VibeSwarmHub.reconnectAttempts = 0;

					// Try to reinitialize
					if (window.VibeSwarmHub.dotNetReference) {
						window.VibeSwarmHub.initialize(window.VibeSwarmHub.dotNetReference);
					} else if (window.VibeSwarmHub.attemptReconnect) {
						window.VibeSwarmHub.attemptReconnect();
					}
				}
			}
		},

		// Notify the app that reconnection completed
		notifyAppReconnected() {
			// Dispatch a custom event that components can listen to
			window.dispatchEvent(
				new CustomEvent("vibeswarm-reconnected", {
					detail: {
						disconnectedDuration: this.connectionLostTime
							? Date.now() - this.connectionLostTime
							: 0,
					},
				}),
			);

			// Also try to notify .NET components
			if (window.VibeSwarmHub?.notifyDotNet) {
				window.VibeSwarmHub.notifyDotNet("OnReconnected");
			}
		},

		// Setup a heartbeat to detect stale connections
		setupHeartbeat() {
			// Check connection health every 30 seconds when visible
			setInterval(() => {
				if (document.visibilityState === "visible" && !this.isReconnecting) {
					this.checkConnectionHealth();
				}
			}, 30000);

			// iOS PWA: More frequent checks when recently foregrounded
			if (this.isIOSPWA) {
				setInterval(() => {
					const timeSinceVisible = Date.now() - this.lastVisibilityChange;
					// Within 2 minutes of becoming visible, check more frequently
					if (
						document.visibilityState === "visible" &&
						timeSinceVisible < 120000 &&
						!this.isReconnecting
					) {
						this.checkConnectionHealth();
					}
				}, 10000);
			}
		},

		// Check connection health silently
		async checkConnectionHealth() {
			// Check if VibeSwarmHub reports disconnected
			if (
				window.VibeSwarmHub &&
				!window.VibeSwarmHub.isConnected &&
				!window.VibeSwarmHub.isConnecting
			) {
				console.log(
					"[ReconnectHandler] SignalR hub disconnected, attempting reconnect",
				);
				this.checkAndReconnect(false);
				return;
			}

			// Check for stale connection (no successful ping in a while)
			const timeSinceLastPing = Date.now() - this.lastSuccessfulPing;
			if (timeSinceLastPing > 90000) {
				// 90 seconds
				console.log("[ReconnectHandler] Connection may be stale, checking...");
				this.checkAndReconnect(false);
			}
		},
	};

	// Initialize when DOM is ready
	if (document.readyState === "loading") {
		document.addEventListener("DOMContentLoaded", () =>
			ReconnectHandler.init(),
		);
	} else {
		ReconnectHandler.init();
	}

	// Expose for debugging
	window.ReconnectHandler = ReconnectHandler;
})();
