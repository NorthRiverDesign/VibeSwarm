// Global SignalR connection manager for VibeSwarm
// Enhanced for iOS PWA with stateful reconnect support
// This maintains a single SignalR connection that persists across page navigation
// and provides global notification capabilities

(function () {
	"use strict";

	// iOS PWA detection
	const isIOS =
		/iPad|iPhone|iPod/.test(navigator.userAgent) && !window.MSStream;
	const isStandalone =
		window.navigator.standalone === true ||
		window.matchMedia("(display-mode: standalone)").matches;
	const isIOSPWA = isIOS && isStandalone;

	const VibeSwarmHub = {
		connection: null,
		isConnecting: false,
		isConnected: false,
		dotNetReference: null,
		reconnectAttempts: 0,
		maxReconnectAttempts: isIOSPWA ? 20 : 10,
		eventHandlers: {},
		pendingSubscriptions: [],
		lastPingTime: Date.now(),
		isIOSPWA: isIOSPWA,

		// Initialize the global SignalR connection with stateful reconnect
		async initialize(dotNetRef) {
			if (this.isConnecting || this.isConnected) {
				// Already connected or connecting, just update the reference
				this.dotNetReference = dotNetRef;
				return true;
			}

			this.dotNetReference = dotNetRef;
			this.isConnecting = true;

			try {
				// Wait for signalR to be available
				await this.waitForSignalR();

				// Build connection with stateful reconnect support
				const builder = new signalR.HubConnectionBuilder()
					.withUrl("/jobhub")
					.withAutomaticReconnect({
						nextRetryDelayInMilliseconds: (retryContext) => {
							// iOS needs longer delays due to WebSocket suspension
							if (isIOSPWA) {
								const iosDelays = [
									0, 2000, 5000, 10000, 15000, 20000, 30000, 45000, 60000,
								];
								return retryContext.previousRetryCount < iosDelays.length
									? iosDelays[retryContext.previousRetryCount]
									: 60000;
							}
							// Standard exponential backoff
							const delays = [0, 1000, 2000, 5000, 10000, 30000, 60000];
							return delays[
								Math.min(retryContext.previousRetryCount, delays.length - 1)
							];
						},
					})
					.configureLogging(signalR.LogLevel.Warning);

				// Enable stateful reconnect for .NET 10+ (buffers messages during brief disconnections)
				if (builder.withStatefulReconnect) {
					builder.withStatefulReconnect({ bufferSize: 100000 });
					console.log("[VibeSwarmHub] Stateful reconnect enabled");
				}

				// Set timeouts optimized for iOS
				if (builder.withServerTimeout) {
					builder.withServerTimeout(isIOSPWA ? 60000 : 30000);
				}
				if (builder.withKeepAliveInterval) {
					builder.withKeepAliveInterval(15000);
				}

				this.connection = builder.build();

				// Set connection properties directly if available
				if (this.connection.serverTimeoutInMilliseconds !== undefined) {
					this.connection.serverTimeoutInMilliseconds = isIOSPWA
						? 60000
						: 30000;
				}
				if (this.connection.keepAliveIntervalInMilliseconds !== undefined) {
					this.connection.keepAliveIntervalInMilliseconds = 15000;
				}

				this.registerEventHandlers();
				this.setupConnectionEvents();

				await this.connection.start();
				this.isConnected = true;
				this.isConnecting = false;
				this.reconnectAttempts = 0;
				this.lastPingTime = Date.now();

				console.log(
					`[VibeSwarmHub] Connected successfully ${isIOSPWA ? "(iOS PWA mode)" : ""}`,
				);

				// Process any pending subscriptions
				await this.processPendingSubscriptions();

				return true;
			} catch (err) {
				this.isConnecting = false;
				console.error("[VibeSwarmHub] Error connecting:", err);
				return false;
			}
		},

		// Wait for SignalR library to be loaded
		waitForSignalR() {
			return new Promise((resolve, reject) => {
				if (typeof signalR !== "undefined") {
					resolve();
					return;
				}

				let attempts = 0;
				const maxAttempts = 50; // 5 seconds total
				const interval = setInterval(() => {
					attempts++;
					if (typeof signalR !== "undefined") {
						clearInterval(interval);
						resolve();
					} else if (attempts >= maxAttempts) {
						clearInterval(interval);
						reject(new Error("SignalR library not loaded"));
					}
				}, 100);
			});
		},

		// Register all SignalR event handlers
		registerEventHandlers() {
			// Job status events
			this.connection.on("JobStatusChanged", (jobId, status) => {
				console.log(`[VibeSwarmHub] JobStatusChanged: ${jobId} -> ${status}`);
				this.notifyDotNet("OnGlobalJobStatusChanged", jobId, status);
				this.triggerLocalHandler("JobStatusChanged", jobId, status);
			});

			this.connection.on("JobActivityUpdated", (jobId, activity, timestamp) => {
				this.notifyDotNet(
					"OnGlobalJobActivityUpdated",
					jobId,
					activity,
					timestamp,
				);
				this.triggerLocalHandler(
					"JobActivityUpdated",
					jobId,
					activity,
					timestamp,
				);
			});

			this.connection.on("JobCreated", (jobId, projectId) => {
				console.log(
					`[VibeSwarmHub] JobCreated: ${jobId} in project ${projectId}`,
				);
				this.notifyDotNet("OnGlobalJobCreated", jobId, projectId);
				this.triggerLocalHandler("JobCreated", jobId, projectId);
			});

			this.connection.on("JobDeleted", (jobId, projectId) => {
				console.log(
					`[VibeSwarmHub] JobDeleted: ${jobId} from project ${projectId}`,
				);
				this.notifyDotNet("OnGlobalJobDeleted", jobId, projectId);
				this.triggerLocalHandler("JobDeleted", jobId, projectId);
			});

			this.connection.on("JobCompleted", (jobId, success, errorMessage) => {
				console.log(
					`[VibeSwarmHub] JobCompleted: ${jobId}, success: ${success}`,
				);
				this.notifyDotNet("OnGlobalJobCompleted", jobId, success, errorMessage);
				this.triggerLocalHandler("JobCompleted", jobId, success, errorMessage);
			});

			this.connection.on("JobListChanged", () => {
				console.log(`[VibeSwarmHub] JobListChanged`);
				this.notifyDotNet("OnGlobalJobListChanged");
				this.triggerLocalHandler("JobListChanged");
			});

			this.connection.on("JobMessageAdded", (jobId) => {
				this.notifyDotNet("OnGlobalJobMessageAdded", jobId);
				this.triggerLocalHandler("JobMessageAdded", jobId);
			});

			this.connection.on("JobHeartbeat", (jobId, timestamp) => {
				this.triggerLocalHandler("JobHeartbeat", jobId, timestamp);
			});

			this.connection.on("JobOutput", (jobId, line, isError, timestamp) => {
				this.triggerLocalHandler("JobOutput", jobId, line, isError, timestamp);
			});

			this.connection.on("ProcessStarted", (jobId, processId, command) => {
				this.notifyDotNet("OnGlobalProcessStarted", jobId, processId, command);
				this.triggerLocalHandler("ProcessStarted", jobId, processId, command);
			});

			this.connection.on(
				"ProcessExited",
				(jobId, processId, exitCode, durationSeconds) => {
					this.notifyDotNet(
						"OnGlobalProcessExited",
						jobId,
						processId,
						exitCode,
						durationSeconds,
					);
					this.triggerLocalHandler(
						"ProcessExited",
						jobId,
						processId,
						exitCode,
						durationSeconds,
					);
				},
			);

			// Global notification events
			this.connection.on("ShowNotification", (title, message, type) => {
				console.log(`[VibeSwarmHub] ShowNotification: ${title}`);
				this.notifyDotNet("OnShowNotification", title, message, type);
			});
		},

		// Setup connection lifecycle events
		setupConnectionEvents() {
			this.connection.onreconnecting((error) => {
				this.isConnected = false;
				console.log("[VibeSwarmHub] Reconnecting...", error?.message || "");
				this.notifyDotNet("OnConnectionStateChanged", "Reconnecting");

				// Dispatch event for reconnect handler
				window.dispatchEvent(
					new CustomEvent("signalr-reconnecting", { detail: { error } }),
				);
			});

			this.connection.onreconnected(async (connectionId) => {
				this.isConnected = true;
				this.reconnectAttempts = 0;
				this.lastPingTime = Date.now();
				console.log(
					"[VibeSwarmHub] Reconnected with connectionId:",
					connectionId,
				);
				this.notifyDotNet("OnConnectionStateChanged", "Connected");

				// Re-process subscriptions after reconnection
				await this.processPendingSubscriptions();

				// Dispatch event for reconnect handler
				window.dispatchEvent(
					new CustomEvent("signalr-reconnected", { detail: { connectionId } }),
				);

				// Update ReconnectHandler's ping time
				if (window.ReconnectHandler) {
					window.ReconnectHandler.lastSuccessfulPing = Date.now();
				}
			});

			this.connection.onclose(async (error) => {
				this.isConnected = false;
				console.log("[VibeSwarmHub] Connection closed", error?.message || "");
				this.notifyDotNet("OnConnectionStateChanged", "Disconnected");

				// Dispatch event for reconnect handler
				window.dispatchEvent(
					new CustomEvent("signalr-closed", { detail: { error } }),
				);

				// Only auto-reconnect if:
				// 1. Not already connecting
				// 2. Page is visible
				// 3. Blazor circuit isn't marked as dead
				if (
					!this.isConnecting &&
					document.visibilityState === "visible" &&
					!window.VibeSwarmReconnect?.circuitDead
				) {
					console.log(
						"[VibeSwarmHub] Attempting auto-reconnect after connection close",
					);
					// Use longer delay for iOS PWA
					const delay = this.isIOSPWA ? 3000 : 2000;
					setTimeout(() => this.attemptReconnect(), delay);
				}
			});
		},

		// Attempt to reconnect the SignalR hub
		async attemptReconnect() {
			if (this.isConnecting || this.isConnected) {
				return;
			}

			// Don't attempt if Blazor circuit is dead
			if (window.VibeSwarmReconnect?.circuitDead) {
				console.log(
					"[VibeSwarmHub] Blazor circuit is dead, skipping SignalR reconnect",
				);
				return;
			}

			this.reconnectAttempts++;
			if (this.reconnectAttempts > this.maxReconnectAttempts) {
				console.log("[VibeSwarmHub] Max reconnect attempts reached");
				return;
			}

			console.log(
				`[VibeSwarmHub] Reconnect attempt ${this.reconnectAttempts}/${this.maxReconnectAttempts}`,
			);

			try {
				this.isConnecting = true;

				// Create a fresh connection with stateful reconnect
				await this.waitForSignalR();

				const builder = new signalR.HubConnectionBuilder()
					.withUrl("/jobhub")
					.withAutomaticReconnect({
						nextRetryDelayInMilliseconds: (retryContext) => {
							if (this.isIOSPWA) {
								const iosDelays = [
									0, 2000, 5000, 10000, 15000, 20000, 30000, 45000, 60000,
								];
								return retryContext.previousRetryCount < iosDelays.length
									? iosDelays[retryContext.previousRetryCount]
									: 60000;
							}
							const delays = [0, 1000, 2000, 5000, 10000, 30000, 60000];
							return delays[
								Math.min(retryContext.previousRetryCount, delays.length - 1)
							];
						},
					})
					.configureLogging(signalR.LogLevel.Warning);

				// Enable stateful reconnect
				if (builder.withStatefulReconnect) {
					builder.withStatefulReconnect({ bufferSize: 100000 });
				}
				if (builder.withServerTimeout) {
					builder.withServerTimeout(this.isIOSPWA ? 60000 : 30000);
				}
				if (builder.withKeepAliveInterval) {
					builder.withKeepAliveInterval(15000);
				}

				this.connection = builder.build();

				this.registerEventHandlers();
				this.setupConnectionEvents();

				await this.connection.start();
				this.isConnected = true;
				this.isConnecting = false;
				this.reconnectAttempts = 0;
				this.lastPingTime = Date.now();

				console.log("[VibeSwarmHub] Reconnected successfully");
				this.notifyDotNet("OnConnectionStateChanged", "Connected");

				// Re-process subscriptions
				await this.processPendingSubscriptions();

				// Dispatch reconnect event
				window.dispatchEvent(
					new CustomEvent("signalr-reconnected", {
						detail: { connectionId: this.connection.connectionId },
					}),
				);
			} catch (err) {
				this.isConnecting = false;
				console.error("[VibeSwarmHub] Reconnect failed:", err);

				// Try again with exponential backoff
				const baseDelay = this.isIOSPWA ? 2000 : 1000;
				const delay = Math.min(
					baseDelay * Math.pow(2, this.reconnectAttempts),
					60000,
				);
				setTimeout(() => this.attemptReconnect(), delay);
			}
		},

		// Notify .NET of events
		notifyDotNet(methodName, ...args) {
			if (this.dotNetReference) {
				try {
					this.dotNetReference.invokeMethodAsync(methodName, ...args);
				} catch (err) {
					// .NET reference may be disposed during navigation
					console.debug(
						`[VibeSwarmHub] Could not invoke ${methodName}:`,
						err.message,
					);
				}
			}
		},

		// Trigger local JavaScript event handlers
		triggerLocalHandler(eventName, ...args) {
			const handlers = this.eventHandlers[eventName];
			if (handlers) {
				handlers.forEach((handler) => {
					try {
						handler(...args);
					} catch (err) {
						console.error(
							`[VibeSwarmHub] Error in handler for ${eventName}:`,
							err,
						);
					}
				});
			}
		},

		// Subscribe to a specific job's updates
		async subscribeToJob(jobId) {
			if (this.isConnected && this.connection) {
				try {
					await this.connection.invoke("SubscribeToJob", jobId);
					console.log(`[VibeSwarmHub] Subscribed to job: ${jobId}`);
				} catch (err) {
					console.error(
						`[VibeSwarmHub] Error subscribing to job ${jobId}:`,
						err,
					);
				}
			} else {
				this.pendingSubscriptions.push({ type: "job", id: jobId });
			}
		},

		// Unsubscribe from a specific job
		async unsubscribeFromJob(jobId) {
			if (this.isConnected && this.connection) {
				try {
					await this.connection.invoke("UnsubscribeFromJob", jobId);
					console.log(`[VibeSwarmHub] Unsubscribed from job: ${jobId}`);
				} catch (err) {
					console.error(
						`[VibeSwarmHub] Error unsubscribing from job ${jobId}:`,
						err,
					);
				}
			}
			// Remove from pending subscriptions if present
			this.pendingSubscriptions = this.pendingSubscriptions.filter(
				(s) => !(s.type === "job" && s.id === jobId),
			);
		},

		// Subscribe to global job list updates
		async subscribeToJobList() {
			if (this.isConnected && this.connection) {
				try {
					await this.connection.invoke("SubscribeToJobList");
					console.log("[VibeSwarmHub] Subscribed to job list");
				} catch (err) {
					console.error("[VibeSwarmHub] Error subscribing to job list:", err);
				}
			} else {
				if (!this.pendingSubscriptions.some((s) => s.type === "jobList")) {
					this.pendingSubscriptions.push({ type: "jobList" });
				}
			}
		},

		// Unsubscribe from job list
		async unsubscribeFromJobList() {
			if (this.isConnected && this.connection) {
				try {
					await this.connection.invoke("UnsubscribeFromJobList");
					console.log("[VibeSwarmHub] Unsubscribed from job list");
				} catch (err) {
					console.error(
						"[VibeSwarmHub] Error unsubscribing from job list:",
						err,
					);
				}
			}
			this.pendingSubscriptions = this.pendingSubscriptions.filter(
				(s) => s.type !== "jobList",
			);
		},

		// Subscribe to a project's updates
		async subscribeToProject(projectId) {
			if (this.isConnected && this.connection) {
				try {
					await this.connection.invoke("SubscribeToProject", projectId);
					console.log(`[VibeSwarmHub] Subscribed to project: ${projectId}`);
				} catch (err) {
					console.error(
						`[VibeSwarmHub] Error subscribing to project ${projectId}:`,
						err,
					);
				}
			} else {
				this.pendingSubscriptions.push({ type: "project", id: projectId });
			}
		},

		// Unsubscribe from a project
		async unsubscribeFromProject(projectId) {
			if (this.isConnected && this.connection) {
				try {
					await this.connection.invoke("UnsubscribeFromProject", projectId);
					console.log(`[VibeSwarmHub] Unsubscribed from project: ${projectId}`);
				} catch (err) {
					console.error(
						`[VibeSwarmHub] Error unsubscribing from project ${projectId}:`,
						err,
					);
				}
			}
			this.pendingSubscriptions = this.pendingSubscriptions.filter(
				(s) => !(s.type === "project" && s.id === projectId),
			);
		},

		// Process pending subscriptions after connection/reconnection
		async processPendingSubscriptions() {
			const subscriptions = [...this.pendingSubscriptions];
			this.pendingSubscriptions = [];

			for (const sub of subscriptions) {
				switch (sub.type) {
					case "job":
						await this.subscribeToJob(sub.id);
						break;
					case "jobList":
						await this.subscribeToJobList();
						break;
					case "project":
						await this.subscribeToProject(sub.id);
						break;
				}
			}
		},

		// Register a local JavaScript event handler
		on(eventName, handler) {
			if (!this.eventHandlers[eventName]) {
				this.eventHandlers[eventName] = [];
			}
			this.eventHandlers[eventName].push(handler);
		},

		// Remove a local JavaScript event handler
		off(eventName, handler) {
			if (this.eventHandlers[eventName]) {
				this.eventHandlers[eventName] = this.eventHandlers[eventName].filter(
					(h) => h !== handler,
				);
			}
		},

		// Update the .NET reference (called when MainLayout re-renders)
		updateDotNetReference(dotNetRef) {
			this.dotNetReference = dotNetRef;
		},

		// Dispose the connection
		async dispose() {
			if (this.connection) {
				try {
					await this.connection.stop();
				} catch (err) {
					console.error("[VibeSwarmHub] Error stopping connection:", err);
				}
				this.connection = null;
			}
			this.isConnected = false;
			this.isConnecting = false;
			this.dotNetReference = null;
			this.eventHandlers = {};
			this.pendingSubscriptions = [];
		},

		// Get connection state
		getConnectionState() {
			if (this.isConnected) return "Connected";
			if (this.isConnecting) return "Connecting";
			return "Disconnected";
		},
	};

	// Expose to window for global access
	window.VibeSwarmHub = VibeSwarmHub;

	// Helper function for scrolling (used by job detail page)
	window.scrollToBottom = function (element) {
		if (element) {
			element.scrollTop = element.scrollHeight;
		}
	};
})();
