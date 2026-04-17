namespace VibeSwarm.Client.Services;

public class NotificationService
{
	private readonly List<ToastNotification> _notifications = new();
	private readonly List<ToastNotification> _history = new();
	private readonly object _lock = new();
	private int _unreadCount = 0;

	public event Action? OnChange;

	public IReadOnlyList<ToastNotification> Notifications
	{
		get { lock (_lock) return _notifications.ToList().AsReadOnly(); }
	}

	public IReadOnlyList<ToastNotification> NotificationHistory
	{
		get { lock (_lock) return _history.AsEnumerable().Reverse().ToList().AsReadOnly(); }
	}

	public int UnreadCount
	{
		get { lock (_lock) return _unreadCount; }
	}

	public bool IsPanelOpen { get; private set; }

	public void OpenPanel()
	{
		IsPanelOpen = true;
		MarkAllRead();
	}

	public void ClosePanel()
	{
		IsPanelOpen = false;
		OnChange?.Invoke();
	}

	public void TogglePanel()
	{
		if (IsPanelOpen) ClosePanel();
		else OpenPanel();
	}

	public void MarkAllRead()
	{
		lock (_lock) _unreadCount = 0;
		OnChange?.Invoke();
	}

	public void ClearHistory()
	{
		lock (_lock)
		{
			_history.Clear();
			_unreadCount = 0;
		}
		OnChange?.Invoke();
	}

	public void ShowSuccess(string message, string? title = null, int durationMs = 5000)
		=> AddNotification(NotificationType.Success, message, title ?? "Success", durationMs);

	public void ShowError(string message, string? title = null, int durationMs = 8000)
		=> AddNotification(NotificationType.Error, message, title ?? "Error", durationMs);

	public void ShowWarning(string message, string? title = null, int durationMs = 6000)
		=> AddNotification(NotificationType.Warning, message, title ?? "Warning", durationMs);

	public void ShowInfo(string message, string? title = null, int durationMs = 5000)
		=> AddNotification(NotificationType.Info, message, title ?? "Info", durationMs);

	public void ShowProjectSuccess(string? projectName, string message, int durationMs = 5000)
		=> ShowSuccess(message, ResolveContextTitle(projectName, "Success"), durationMs);

	public void ShowProjectError(string? projectName, string message, int durationMs = 8000)
		=> ShowError(message, ResolveContextTitle(projectName, "Error"), durationMs);

	public void ShowJobCompleted(Guid jobId, bool success, string? projectName = null, string? errorMessage = null)
	{
		var targetUrl = $"/jobs/view/{jobId}";
		if (success)
		{
			var message = projectName != null ? $"Job in '{projectName}' completed successfully" : "Job completed successfully";
			Show(message, "Job Completed", NotificationType.Success, 5000, "View Job", targetUrl);
		}
		else
		{
			var message = errorMessage ?? (projectName != null ? $"Job in '{projectName}' failed" : "Job failed");
			Show(message, "Job Failed", NotificationType.Error, 8000, "View Job", targetUrl);
		}
	}

	public void Show(string message, string title, NotificationType type, int durationMs = 5000, string? actionLabel = null, string? actionUrl = null)
	{
		AddNotification(new ToastNotification
		{
			Id = Guid.NewGuid(),
			Title = title,
			Message = message,
			Type = type,
			CreatedAt = DateTime.UtcNow,
			DurationMs = durationMs,
			ActionLabel = actionLabel,
			ActionUrl = actionUrl
		});
	}

	public void Remove(Guid id)
	{
		lock (_lock)
		{
			var notification = _notifications.FirstOrDefault(n => n.Id == id);
			if (notification != null)
			{
				_notifications.Remove(notification);
				OnChange?.Invoke();
			}
		}
	}

	public void Clear()
	{
		lock (_lock)
		{
			_notifications.Clear();
			OnChange?.Invoke();
		}
	}

	private void AddNotification(NotificationType type, string message, string title, int durationMs)
		=> AddNotification(new ToastNotification { Id = Guid.NewGuid(), Title = title, Message = message, Type = type, CreatedAt = DateTime.UtcNow, DurationMs = durationMs });

	private void AddNotification(ToastNotification notification)
	{
		lock (_lock)
		{
			while (_notifications.Count >= 10) _notifications.RemoveAt(0);
			_notifications.Add(notification);
			while (_history.Count >= 50) _history.RemoveAt(0);
			_history.Add(notification);
			_unreadCount++;
		}
		OnChange?.Invoke();
	}

	private static string ResolveContextTitle(string? contextName, string fallbackTitle)
		=> string.IsNullOrWhiteSpace(contextName) ? fallbackTitle : contextName.Trim();
}

public class ToastNotification
{
	public Guid Id { get; set; }
	public string Title { get; set; } = string.Empty;
	public string Message { get; set; } = string.Empty;
	public NotificationType Type { get; set; }
	public DateTime CreatedAt { get; set; }
	public int DurationMs { get; set; }
	public string? ActionLabel { get; set; }
	public string? ActionUrl { get; set; }
	public bool HasAction => !string.IsNullOrWhiteSpace(ActionLabel) && !string.IsNullOrWhiteSpace(ActionUrl);
}

public enum NotificationType
{
	Success,
	Error,
	Warning,
	Info
}
