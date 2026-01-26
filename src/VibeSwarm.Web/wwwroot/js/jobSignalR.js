// SignalR connection for Job detail page
// Uses the global VibeSwarmHub for connection management
let dotNetReference = null;
let currentJobId = null;
let handlers = {};

export async function initializeJobHub(jobId, dotNetRef) {
	dotNetReference = dotNetRef;
	currentJobId = jobId;

	try {
		// Wait for the global hub to be available
		await waitForGlobalHub();

		// Register local handlers for job-specific events
		handlers.JobStatusChanged = (eventJobId, status) => {
			if (eventJobId === jobId) {
				console.log(`[SignalR] JobStatusChanged: ${eventJobId} -> ${status}`);
				if (dotNetReference) {
					dotNetReference.invokeMethodAsync(
						"OnJobStatusChanged",
						eventJobId,
						status,
					);
				}
			}
		};

		handlers.JobActivityUpdated = (eventJobId, activity, timestamp) => {
			if (eventJobId === jobId) {
				console.log(
					`[SignalR] JobActivityUpdated: ${eventJobId} -> ${activity}`,
				);
				if (dotNetReference) {
					dotNetReference.invokeMethodAsync(
						"OnJobActivityUpdated",
						eventJobId,
						activity,
						timestamp,
					);
				}
			}
		};

		handlers.JobMessageAdded = (eventJobId) => {
			if (eventJobId === jobId) {
				console.log(`[SignalR] JobMessageAdded: ${eventJobId}`);
				if (dotNetReference) {
					dotNetReference.invokeMethodAsync("OnJobMessageAdded", eventJobId);
				}
			}
		};

		handlers.JobCompleted = (eventJobId, success, errorMessage) => {
			if (eventJobId === jobId) {
				console.log(
					`[SignalR] JobCompleted: ${eventJobId}, success: ${success}`,
				);
				if (dotNetReference) {
					dotNetReference.invokeMethodAsync(
						"OnJobCompleted",
						eventJobId,
						success,
						errorMessage,
					);
				}
			}
		};

		handlers.JobHeartbeat = (eventJobId, timestamp) => {
			if (eventJobId === jobId) {
				console.log(`[SignalR] JobHeartbeat: ${eventJobId} at ${timestamp}`);
			}
		};

		handlers.JobOutput = (eventJobId, line, isError, timestamp) => {
			if (eventJobId === jobId && dotNetReference) {
				dotNetReference.invokeMethodAsync(
					"OnJobOutput",
					eventJobId,
					line,
					isError,
					timestamp,
				);
			}
		};

		handlers.ProcessStarted = (eventJobId, processId, command) => {
			if (eventJobId === jobId) {
				console.log(
					`[SignalR] ProcessStarted: ${eventJobId}, PID: ${processId}`,
				);
				if (dotNetReference) {
					dotNetReference.invokeMethodAsync(
						"OnProcessStarted",
						eventJobId,
						processId,
						command,
					);
				}
			}
		};

		handlers.ProcessExited = (
			eventJobId,
			processId,
			exitCode,
			durationSeconds,
		) => {
			if (eventJobId === jobId) {
				console.log(
					`[SignalR] ProcessExited: ${eventJobId}, PID: ${processId}, ExitCode: ${exitCode}`,
				);
				if (dotNetReference) {
					dotNetReference.invokeMethodAsync(
						"OnProcessExited",
						eventJobId,
						processId,
						exitCode,
						durationSeconds,
					);
				}
			}
		};

		handlers.JobGitDiffUpdated = (eventJobId, hasChanges) => {
			if (eventJobId === jobId) {
				console.log(
					`[SignalR] JobGitDiffUpdated: ${eventJobId}, hasChanges: ${hasChanges}`,
				);
				if (dotNetReference) {
					dotNetReference.invokeMethodAsync(
						"OnJobGitDiffUpdated",
						eventJobId,
						hasChanges,
					);
				}
			}
		};

		handlers.JobInteractionRequired = (
			eventJobId,
			prompt,
			interactionType,
			choices,
			defaultResponse,
		) => {
			if (eventJobId === jobId) {
				console.log(
					`[SignalR] JobInteractionRequired: ${eventJobId}, type: ${interactionType}, prompt: ${prompt}`,
				);
				if (dotNetReference) {
					dotNetReference.invokeMethodAsync(
						"OnJobInteractionRequired",
						eventJobId,
						prompt,
						interactionType,
						choices,
						defaultResponse,
					);
				}
			}
		};

		handlers.JobResumed = (eventJobId) => {
			if (eventJobId === jobId) {
				console.log(`[SignalR] JobResumed: ${eventJobId}`);
				if (dotNetReference) {
					dotNetReference.invokeMethodAsync("OnJobResumed", eventJobId);
				}
			}
		};

		// Register handlers with the global hub
		Object.entries(handlers).forEach(([event, handler]) => {
			window.VibeSwarmHub.on(event, handler);
		});

		// Subscribe to job updates via the global hub
		await window.VibeSwarmHub.subscribeToJob(jobId);
		console.log(
			`[SignalR] Connected via global hub, subscribed to job: ${jobId}`,
		);
	} catch (err) {
		console.error("[SignalR] Error connecting:", err);
	}
}

export async function disposeJobHub() {
	try {
		// Unsubscribe from job updates
		if (currentJobId) {
			await window.VibeSwarmHub?.unsubscribeFromJob(currentJobId);
		}
	} catch (err) {
		console.error("Error unsubscribing from job:", err);
	}

	// Remove local handlers
	if (window.VibeSwarmHub) {
		Object.entries(handlers).forEach(([event, handler]) => {
			window.VibeSwarmHub.off(event, handler);
		});
	}

	handlers = {};
	dotNetReference = null;
	currentJobId = null;
}

export async function submitInteractionResponse(jobId, response) {
	try {
		await waitForGlobalHub();

		if (!window.VibeSwarmHub || !window.VibeSwarmHub.connection) {
			console.error("[SignalR] Cannot submit response: Hub not connected");
			return false;
		}

		console.log(
			`[SignalR] Submitting interaction response for job ${jobId}: ${response}`,
		);
		const result = await window.VibeSwarmHub.connection.invoke(
			"SubmitInteractionResponse",
			jobId,
			response,
		);
		console.log(`[SignalR] Interaction response submitted, result: ${result}`);
		return result;
	} catch (err) {
		console.error("[SignalR] Error submitting interaction response:", err);
		return false;
	}
}

async function waitForGlobalHub() {
	return new Promise((resolve, reject) => {
		if (window.VibeSwarmHub && window.VibeSwarmHub.isConnected) {
			resolve();
			return;
		}

		let attempts = 0;
		const maxAttempts = 50; // 5 seconds total
		const interval = setInterval(async () => {
			attempts++;
			if (window.VibeSwarmHub) {
				if (window.VibeSwarmHub.isConnected) {
					clearInterval(interval);
					resolve();
				} else if (!window.VibeSwarmHub.isConnecting) {
					// Try to initialize if not already connecting
					try {
						await window.VibeSwarmHub.initialize(null);
					} catch {}
				}
			}

			if (attempts >= maxAttempts) {
				clearInterval(interval);
				// Even if not fully connected, we can still register handlers
				resolve();
			}
		}, 100);
	});
}

// Global helper function for scrolling
window.scrollToBottom = function (element) {
	if (element) {
		element.scrollTop = element.scrollHeight;
	}
};
