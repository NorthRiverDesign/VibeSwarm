let hubConnection = null;
let dotNetReference = null;

export async function initializeJobHub(jobId, dotNetRef) {
	dotNetReference = dotNetRef;

	// Import SignalR from local library
	if (!window.signalR) {
		await loadSignalR();
	}

	// Create connection
	hubConnection = new signalR.HubConnectionBuilder()
		.withUrl("/jobhub")
		.withAutomaticReconnect([0, 1000, 2000, 5000, 10000])
		.configureLogging(signalR.LogLevel.Warning)
		.build();

	// Register event handlers
	hubConnection.on("JobStatusChanged", (jobId, status) => {
		console.log(`[SignalR] JobStatusChanged: ${jobId} -> ${status}`);
		if (dotNetReference) {
			dotNetReference.invokeMethodAsync("OnJobStatusChanged", jobId, status);
		}
	});

	hubConnection.on("JobActivityUpdated", (jobId, activity, timestamp) => {
		console.log(`[SignalR] JobActivityUpdated: ${jobId} -> ${activity}`);
		if (dotNetReference) {
			dotNetReference.invokeMethodAsync(
				"OnJobActivityUpdated",
				jobId,
				activity,
				timestamp,
			);
		}
	});

	hubConnection.on("JobMessageAdded", (jobId) => {
		console.log(`[SignalR] JobMessageAdded: ${jobId}`);
		if (dotNetReference) {
			dotNetReference.invokeMethodAsync("OnJobMessageAdded", jobId);
		}
	});

	hubConnection.on("JobCompleted", (jobId, success, errorMessage) => {
		console.log(`[SignalR] JobCompleted: ${jobId}, success: ${success}`);
		if (dotNetReference) {
			dotNetReference.invokeMethodAsync(
				"OnJobCompleted",
				jobId,
				success,
				errorMessage,
			);
		}
	});

	hubConnection.on("JobHeartbeat", (jobId, timestamp) => {
		console.log(`[SignalR] JobHeartbeat: ${jobId} at ${timestamp}`);
		// Heartbeats can be used to detect if job is still alive
	});

	// Real-time output streaming
	hubConnection.on("JobOutput", (jobId, line, isError, timestamp) => {
		// Don't log every line to avoid console spam
		if (dotNetReference) {
			dotNetReference.invokeMethodAsync(
				"OnJobOutput",
				jobId,
				line,
				isError,
				timestamp,
			);
		}
	});

	// Process lifecycle events
	hubConnection.on("ProcessStarted", (jobId, processId, command) => {
		console.log(`[SignalR] ProcessStarted: ${jobId}, PID: ${processId}`);
		if (dotNetReference) {
			dotNetReference.invokeMethodAsync(
				"OnProcessStarted",
				jobId,
				processId,
				command,
			);
		}
	});

	hubConnection.on(
		"ProcessExited",
		(jobId, processId, exitCode, durationSeconds) => {
			console.log(
				`[SignalR] ProcessExited: ${jobId}, PID: ${processId}, ExitCode: ${exitCode}`,
			);
			if (dotNetReference) {
				dotNetReference.invokeMethodAsync(
					"OnProcessExited",
					jobId,
					processId,
					exitCode,
					durationSeconds,
				);
			}
		},
	);

	// Start connection
	try {
		await hubConnection.start();
		console.log("SignalR connected successfully");

		// Subscribe to the specific job's updates
		await hubConnection.invoke("SubscribeToJob", jobId);
		console.log(`Subscribed to job: ${jobId}`);
	} catch (err) {
		console.error("Error connecting to SignalR:", err);
		setTimeout(() => initializeJobHub(jobId, dotNetRef), 5000);
	}

	// Handle reconnection
	hubConnection.onreconnected(async (connectionId) => {
		console.log("SignalR reconnected");
		// Re-subscribe to the job after reconnection
		await hubConnection.invoke("SubscribeToJob", jobId);
	});

	hubConnection.onclose(async () => {
		console.log("SignalR connection closed");
	});
}

export async function disposeJobHub() {
	if (hubConnection) {
		try {
			await hubConnection.stop();
			console.log("SignalR connection stopped");
		} catch (err) {
			console.error("Error stopping SignalR:", err);
		}
		hubConnection = null;
	}
	dotNetReference = null;
}

async function loadSignalR() {
	return new Promise((resolve, reject) => {
		if (window.signalR) {
			resolve();
			return;
		}

		const script = document.createElement("script");
		script.src = "/lib/signalr/signalr.min.js";
		script.onload = () => resolve();
		script.onerror = () => reject(new Error("Failed to load SignalR"));
		document.head.appendChild(script);
	});
}

// Global helper function for scrolling
window.scrollToBottom = function (element) {
	if (element) {
		element.scrollTop = element.scrollHeight;
	}
};
