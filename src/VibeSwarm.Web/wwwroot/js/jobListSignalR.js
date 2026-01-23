// SignalR connection for Jobs list page
let connection = null;
let dotNetReference = null;

export async function initializeJobListHub(dotNetRef) {
	dotNetReference = dotNetRef;

	try {
		connection = new signalR.HubConnectionBuilder()
			.withUrl("/jobhub")
			.withAutomaticReconnect([0, 1000, 2000, 5000, 10000])
			.configureLogging(signalR.LogLevel.Warning)
			.build();

		// Subscribe to global job list events
		connection.on("JobStatusChanged", (jobId, status) => {
			console.log(`[JobListHub] JobStatusChanged: ${jobId} -> ${status}`);
			dotNetReference.invokeMethodAsync("OnJobStatusChanged", jobId, status);
		});

		connection.on("JobActivityUpdated", (jobId, activity, timestamp) => {
			console.log(`[JobListHub] JobActivityUpdated: ${jobId} -> ${activity}`);
			dotNetReference.invokeMethodAsync(
				"OnJobActivityUpdated",
				jobId,
				activity,
				timestamp,
			);
		});

		connection.on("JobCreated", (jobId, projectId) => {
			console.log(`[JobListHub] JobCreated: ${jobId} in project ${projectId}`);
			dotNetReference.invokeMethodAsync("OnJobCreated", jobId, projectId);
		});

		connection.on("JobDeleted", (jobId, projectId) => {
			console.log(
				`[JobListHub] JobDeleted: ${jobId} from project ${projectId}`,
			);
			dotNetReference.invokeMethodAsync("OnJobDeleted", jobId, projectId);
		});

		connection.on("JobCompleted", (jobId, success, errorMessage) => {
			console.log(`[JobListHub] JobCompleted: ${jobId}, success: ${success}`);
			dotNetReference.invokeMethodAsync(
				"OnJobCompleted",
				jobId,
				success,
				errorMessage,
			);
		});

		connection.on("JobListChanged", () => {
			console.log(`[JobListHub] JobListChanged`);
			dotNetReference.invokeMethodAsync("OnJobListChanged");
		});

		connection.onreconnected(() => {
			console.log("[JobListHub] Reconnected, re-subscribing to job list");
			connection.invoke("SubscribeToJobList");
		});

		await connection.start();
		console.log("[JobListHub] Connected");

		// Subscribe to global job list updates
		await connection.invoke("SubscribeToJobList");
		console.log("[JobListHub] Subscribed to job list");
	} catch (err) {
		console.error("[JobListHub] Error connecting:", err);
	}
}

export async function disposeJobListHub() {
	if (connection) {
		try {
			await connection.invoke("UnsubscribeFromJobList");
		} catch {}

		try {
			await connection.stop();
		} catch {}

		connection = null;
	}
	dotNetReference = null;
}
