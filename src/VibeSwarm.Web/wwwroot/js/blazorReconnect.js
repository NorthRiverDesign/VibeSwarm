// Blazor Server PWA Reconnection Handler
// Handles reconnection when the app returns from background on iOS/mobile devices
(function () {
	"use strict";

	const ReconnectHandler = {
		maxReconnectAttempts: 15,
		reconnectAttempts: 0,
		reconnectInterval: null,
		isReconnecting: false,
		lastVisibilityChange: Date.now(),
		connectionLostTime: null,

		init() {
			this.setupVisibilityHandler();
			this.setupBlazorReconnectHandler();
			this.setupHeartbeat();
			console.log("[ReconnectHandler] Initialized");
		},

		// Handle page visibility changes (app going to/from background)
		setupVisibilityHandler() {
			document.addEventListener("visibilitychange", () => {
				if (document.visibilityState === "visible") {
					const timeSinceHidden = Date.now() - this.lastVisibilityChange;
					console.log(
						`[ReconnectHandler] App became visible after ${Math.round(timeSinceHidden / 1000)}s`,
					);

					// If app was hidden for more than 30 seconds, check connection
					if (timeSinceHidden > 30000) {
						this.checkAndReconnect();
					}
				} else {
					this.lastVisibilityChange = Date.now();
					console.log("[ReconnectHandler] App going to background");
				}
			});

			// iOS-specific: handle pageshow event for PWA
			window.addEventListener("pageshow", (event) => {
				if (event.persisted) {
					console.log("[ReconnectHandler] Page restored from bfcache");
					this.checkAndReconnect();
				}
			});

			// Handle focus events (additional trigger for reconnection)
			window.addEventListener("focus", () => {
				const timeSinceHidden = Date.now() - this.lastVisibilityChange;
				if (timeSinceHidden > 60000) {
					console.log(
						"[ReconnectHandler] Window focused after extended period",
					);
					this.checkAndReconnect();
				}
			});

			// Handle online/offline events
			window.addEventListener("online", () => {
				console.log("[ReconnectHandler] Network came online");
				setTimeout(() => this.checkAndReconnect(), 1000);
			});
		},

		// Override Blazor's default reconnection behavior
		setupBlazorReconnectHandler() {
			// Wait for Blazor to be ready
			const waitForBlazor = setInterval(() => {
				if (window.Blazor) {
					clearInterval(waitForBlazor);
					this.configureBlazorReconnect();
				}
			}, 100);

			// Timeout after 10 seconds
			setTimeout(() => clearInterval(waitForBlazor), 10000);
		},

		configureBlazorReconnect() {
			// Store reference to original start function
			const originalStart = Blazor.start;

			// Hook into Blazor's reconnection events
			Blazor.defaultReconnectionHandler = {
				_reconnectionDisplay: null,

				onConnectionDown: (options, error) => {
					console.log("[ReconnectHandler] Blazor connection down", error);
					this.connectionLostTime = Date.now();
					this.showReconnectModal();
					this.startReconnectAttempts();
				},

				onConnectionUp: () => {
					console.log("[ReconnectHandler] Blazor connection restored");
					this.connectionLostTime = null;
					this.reconnectAttempts = 0;
					this.isReconnecting = false;
					this.hideReconnectModal();
					this.clearReconnectInterval();

					// Reinitialize the global SignalR hub
					this.reinitializeSignalR();
				},
			};

			console.log("[ReconnectHandler] Blazor reconnection handler configured");
		},

		// Check if connection is alive and reconnect if needed
		async checkAndReconnect() {
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
					this.showReconnectModal();
					this.startReconnectAttempts();
				} else {
					console.log("[ReconnectHandler] Blazor connection is healthy");
					// Also reinitialize SignalR hub in case it disconnected
					this.reinitializeSignalR();
				}
			} catch (error) {
				console.error("[ReconnectHandler] Error checking connection:", error);
				this.showReconnectModal();
				this.startReconnectAttempts();
			}
		},

		// Check Blazor connection status
		isBlazorConnected() {
			return new Promise((resolve) => {
				// Try to invoke a simple operation
				// If Blazor is disconnected, this will fail
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

			this.reconnectInterval = setInterval(() => {
				this.reconnectAttempts++;
				console.log(
					`[ReconnectHandler] Reconnect attempt ${this.reconnectAttempts}/${this.maxReconnectAttempts}`,
				);

				// Update modal message
				this.updateReconnectMessage(
					`Reconnecting... (attempt ${this.reconnectAttempts})`,
				);

				if (this.reconnectAttempts >= this.maxReconnectAttempts) {
					this.handleReconnectFailure();
				} else {
					// Try to reconnect by triggering Blazor's internal reconnect
					this.triggerBlazorReconnect();
				}
			}, 3000);
		},

		// Trigger Blazor's reconnection mechanism
		triggerBlazorReconnect() {
			try {
				// Try to find and click the reconnect button if it exists
				const reconnectButton = document.querySelector(
					"[data-blazor-reconnect-click]",
				);
				if (reconnectButton) {
					reconnectButton.click();
				}

				// Also try Blazor's internal reconnect
				if (window.Blazor && window.Blazor.reconnect) {
					window.Blazor.reconnect();
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
				"[ReconnectHandler] Max reconnect attempts reached, showing reload button",
			);

			this.updateReconnectMessage("Connection lost. Please reload the app.");
			this.showReloadButton();
		},

		// Clear reconnection interval
		clearReconnectInterval() {
			if (this.reconnectInterval) {
				clearInterval(this.reconnectInterval);
				this.reconnectInterval = null;
			}
		},

		// Show the reconnection modal
		showReconnectModal() {
			const modal = document.getElementById("components-reconnect-modal");
			if (modal) {
				modal.classList.add("components-reconnect-show");
				this.hideReloadButton();
			}
		},

		// Hide the reconnection modal
		hideReconnectModal() {
			const modal = document.getElementById("components-reconnect-modal");
			if (modal) {
				modal.classList.remove("components-reconnect-show");
			}
		},

		// Update the reconnect message
		updateReconnectMessage(message) {
			const messageEl = document.querySelector(".reconnect-message");
			if (messageEl) {
				messageEl.textContent = message;
			}
		},

		// Show the reload button
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

		// Hide the reload button
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
					window.VibeSwarmHub.connection = null;

					// The hub will be reinitialized on next page render
					// or we can try to reinitialize it now
					if (window.VibeSwarmHub.dotNetReference) {
						window.VibeSwarmHub.initialize(window.VibeSwarmHub.dotNetReference);
					}
				}
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
				this.checkAndReconnect();
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
