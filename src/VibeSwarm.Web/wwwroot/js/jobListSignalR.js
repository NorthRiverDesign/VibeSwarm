// SignalR connection for Jobs list page
// Uses the global VibeSwarmHub for connection management
let dotNetReference = null;
let handlers = {};

export async function initializeJobListHub(dotNetRef) {
	dotNetReference = dotNetRef;

	try {
		// Wait for the global hub to be available
		await waitForGlobalHub();

		// Register local handlers for job list events
		handlers.JobStatusChanged = (jobId, status) => {
			console.log(`[JobListHub] JobStatusChanged: ${jobId} -> ${status}`);
			if (dotNetReference) {
				dotNetReference.invokeMethodAsync("OnJobStatusChanged", jobId, status);
			}
		};

		handlers.JobActivityUpdated = (jobId, activity, timestamp) => {
			console.log(`[JobListHub] JobActivityUpdated: ${jobId} -> ${activity}`);
			if (dotNetReference) {
				dotNetReference.invokeMethodAsync(
					"OnJobActivityUpdated",
					jobId,
					activity,
					timestamp,
				);
			}
		};

		handlers.JobCreated = (jobId, projectId) => {
			console.log(`[JobListHub] JobCreated: ${jobId} in project ${projectId}`);
			if (dotNetReference) {
				dotNetReference.invokeMethodAsync("OnJobCreated", jobId, projectId);
			}
		};

		handlers.JobDeleted = (jobId, projectId) => {
			console.log(
				`[JobListHub] JobDeleted: ${jobId} from project ${projectId}`,
			);
			if (dotNetReference) {
				dotNetReference.invokeMethodAsync("OnJobDeleted", jobId, projectId);
			}
		};

		handlers.JobCompleted = (jobId, success, errorMessage) => {
			console.log(`[JobListHub] JobCompleted: ${jobId}, success: ${success}`);
			if (dotNetReference) {
				dotNetReference.invokeMethodAsync(
					"OnJobCompleted",
					jobId,
					success,
					errorMessage,
				);
			}
		};

		handlers.JobListChanged = () => {
			console.log(`[JobListHub] JobListChanged`);
			if (dotNetReference) {
				dotNetReference.invokeMethodAsync("OnJobListChanged");
			}
		};

		// Register handlers with the global hub
		Object.entries(handlers).forEach(([event, handler]) => {
			window.VibeSwarmHub.on(event, handler);
		});

		// Subscribe to job list updates via the global hub
		await window.VibeSwarmHub.subscribeToJobList();
		console.log("[JobListHub] Connected via global hub");
	} catch (err) {
		console.error("[JobListHub] Error connecting:", err);
	}
}

export async function disposeJobListHub() {
	try {
		// Unsubscribe from job list updates
		await window.VibeSwarmHub?.unsubscribeFromJobList();
	} catch {}

	// Remove local handlers
	if (window.VibeSwarmHub) {
		Object.entries(handlers).forEach(([event, handler]) => {
			window.VibeSwarmHub.off(event, handler);
		});
	}

	handlers = {};
	dotNetReference = null;
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
				// They'll work once the connection is established
				resolve();
			}
		}, 100);
	});
}
