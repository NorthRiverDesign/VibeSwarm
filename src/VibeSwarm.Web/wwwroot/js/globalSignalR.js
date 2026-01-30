// Global SignalR connection manager for VibeSwarm
// This maintains a single SignalR connection that persists across page navigation
// and provides global notification capabilities

(function () {
	"use strict";

	const VibeSwarmHub = {
		connection: null,
		isConnecting: false,
		isConnected: false,
		dotNetReference: null,
		reconnectAttempts: 0,
		maxReconnectAttempts: 10,
		eventHandlers: {},
		pendingSubscriptions: [],

		// Initialize the global SignalR connection
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

				this.connection = new signalR.HubConnectionBuilder()
					.withUrl("/jobhub")
					.withAutomaticReconnect({
						nextRetryDelayInMilliseconds: (retryContext) => {
							// Exponential backoff: 0, 1s, 2s, 5s, 10s, 30s, then 60s
							const delays = [0, 1000, 2000, 5000, 10000, 30000, 60000];
							return delays[
								Math.min(retryContext.previousRetryCount, delays.length - 1)
							];
						},
					})
					.configureLogging(signalR.LogLevel.Warning)
					.build();

				this.registerEventHandlers();
				this.setupConnectionEvents();

				await this.connection.start();
				this.isConnected = true;
				this.isConnecting = false;
				this.reconnectAttempts = 0;

				console.log("[VibeSwarmHub] Connected successfully");

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
				console.log("[VibeSwarmHub] Reconnecting...", error);
				this.notifyDotNet("OnConnectionStateChanged", "Reconnecting");
			});

			this.connection.onreconnected(async (connectionId) => {
				this.isConnected = true;
				this.reconnectAttempts = 0;
				console.log("[VibeSwarmHub] Reconnected");
				this.notifyDotNet("OnConnectionStateChanged", "Connected");

				// Re-process subscriptions after reconnection
				await this.processPendingSubscriptions();
			});

			this.connection.onclose(async (error) => {
				this.isConnected = false;
				console.log("[VibeSwarmHub] Connection closed", error);
				this.notifyDotNet("OnConnectionStateChanged", "Disconnected");

				// Auto-reconnect after connection closed (handles iOS background suspension)
				if (!this.isConnecting && document.visibilityState === "visible") {
					console.log(
						"[VibeSwarmHub] Attempting auto-reconnect after connection close",
					);
					setTimeout(() => this.attemptReconnect(), 2000);
				}
			});
		},

		// Attempt to reconnect the SignalR hub
		async attemptReconnect() {
			if (this.isConnecting || this.isConnected) {
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

				// Create a fresh connection
				await this.waitForSignalR();

				this.connection = new signalR.HubConnectionBuilder()
					.withUrl("/jobhub")
					.withAutomaticReconnect({
						nextRetryDelayInMilliseconds: (retryContext) => {
							const delays = [0, 1000, 2000, 5000, 10000, 30000, 60000];
							return delays[
								Math.min(retryContext.previousRetryCount, delays.length - 1)
							];
						},
					})
					.configureLogging(signalR.LogLevel.Warning)
					.build();

				this.registerEventHandlers();
				this.setupConnectionEvents();

				await this.connection.start();
				this.isConnected = true;
				this.isConnecting = false;
				this.reconnectAttempts = 0;

				console.log("[VibeSwarmHub] Reconnected successfully");
				this.notifyDotNet("OnConnectionStateChanged", "Connected");

				// Re-process subscriptions
				await this.processPendingSubscriptions();
			} catch (err) {
				this.isConnecting = false;
				console.error("[VibeSwarmHub] Reconnect failed:", err);

				// Try again with exponential backoff
				const delay = Math.min(
					1000 * Math.pow(2, this.reconnectAttempts),
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
