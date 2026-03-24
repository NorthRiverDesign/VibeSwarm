namespace VibeSwarm.Client.Services;

/// <summary>
/// Service for managing toast notifications throughout the application.
/// This service is scoped to the circuit, so all components in the same Blazor circuit share the same instance.
/// </summary>
public class NotificationService
{
	private readonly List<ToastNotification> _notifications = new();
	private readonly List<ToastNotification> _history = new();
	private readonly object _lock = new();
	private int _unreadCount = 0;

	/// <summary>
	/// Event raised when notifications change
	/// </summary>
	public event Action? OnChange;

	/// <summary>
	/// Get all active notifications
	/// </summary>
	public IReadOnlyList<ToastNotification> Notifications
	{
		get
		{
			lock (_lock)
			{
				return _notifications.ToList().AsReadOnly();
			}
		}
	}

	/// <summary>
	/// Get notification history (newest-first, max 50 entries)
	/// </summary>
	public IReadOnlyList<ToastNotification> NotificationHistory
	{
		get
		{
			lock (_lock)
			{
				return _history.AsEnumerable().Reverse().ToList().AsReadOnly();
			}
		}
	}

	/// <summary>
	/// Number of unread notifications since last MarkAllRead()
	/// </summary>
	public int UnreadCount
	{
		get { lock (_lock) return _unreadCount; }
	}

	/// <summary>
	/// Whether the notifications panel is currently open
	/// </summary>
	public bool IsPanelOpen { get; private set; }

	/// <summary>
	/// Open the notifications panel
	/// </summary>
	public void OpenPanel()
	{
		IsPanelOpen = true;
		MarkAllRead();
	}

	/// <summary>
	/// Close the notifications panel
	/// </summary>
	public void ClosePanel()
	{
		IsPanelOpen = false;
		OnChange?.Invoke();
	}

	/// <summary>
	/// Toggle the notifications panel open/closed
	/// </summary>
	public void TogglePanel()
	{
		if (IsPanelOpen) ClosePanel();
		else OpenPanel();
	}

	/// <summary>
	/// Mark all notifications as read (resets unread count)
	/// </summary>
	public void MarkAllRead()
	{
		lock (_lock)
		{
			_unreadCount = 0;
		}
		OnChange?.Invoke();
	}

	/// <summary>
	/// Clear the notification history
	/// </summary>
	public void ClearHistory()
	{
		lock (_lock)
		{
			_history.Clear();
			_unreadCount = 0;
		}
		OnChange?.Invoke();
	}

	/// <summary>
	/// Show a success notification
	/// </summary>
	public void ShowSuccess(string message, string? title = null, int durationMs = 5000)
	{
		AddNotification(new ToastNotification
		{
			Id = Guid.NewGuid(),
			Title = title ?? "Success",
			Message = message,
			Type = NotificationType.Success,
			CreatedAt = DateTime.UtcNow,
			DurationMs = durationMs
		});
	}

	/// <summary>
	/// Show an error notification
	/// </summary>
	public void ShowError(string message, string? title = null, int durationMs = 8000)
	{
		AddNotification(new ToastNotification
		{
			Id = Guid.NewGuid(),
			Title = title ?? "Error",
			Message = message,
			Type = NotificationType.Error,
			CreatedAt = DateTime.UtcNow,
			DurationMs = durationMs
		});
	}

	/// <summary>
	/// Show a warning notification
	/// </summary>
	public void ShowWarning(string message, string? title = null, int durationMs = 6000)
	{
		AddNotification(new ToastNotification
		{
			Id = Guid.NewGuid(),
			Title = title ?? "Warning",
			Message = message,
			Type = NotificationType.Warning,
			CreatedAt = DateTime.UtcNow,
			DurationMs = durationMs
		});
	}

	/// <summary>
	/// Show an info notification
	/// </summary>
	public void ShowInfo(string message, string? title = null, int durationMs = 5000)
	{
		AddNotification(new ToastNotification
		{
			Id = Guid.NewGuid(),
			Title = title ?? "Info",
			Message = message,
			Type = NotificationType.Info,
			CreatedAt = DateTime.UtcNow,
			DurationMs = durationMs
		});
	}

	/// <summary>
	/// Show a job completion notification
	/// </summary>
	public void ShowJobCompleted(Guid jobId, bool success, string? projectName = null, string? errorMessage = null)
	{
		if (success)
		{
			var message = projectName != null
				? $"Job in '{projectName}' completed successfully"
				: "Job completed successfully";
			ShowSuccess(message, "Job Completed");
		}
		else
		{
			var message = errorMessage ?? (projectName != null
				? $"Job in '{projectName}' failed"
				: "Job failed");
			ShowError(message, "Job Failed");
		}
	}

	/// <summary>
	/// Show a notification with custom parameters
	/// </summary>
	public void Show(string message, string title, NotificationType type, int durationMs = 5000)
	{
		AddNotification(new ToastNotification
		{
			Id = Guid.NewGuid(),
			Title = title,
			Message = message,
			Type = type,
			CreatedAt = DateTime.UtcNow,
			DurationMs = durationMs
		});
	}

	/// <summary>
	/// Remove a notification by ID
	/// </summary>
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

	/// <summary>
	/// Clear all notifications
	/// </summary>
	public void Clear()
	{
		lock (_lock)
		{
			_notifications.Clear();
			OnChange?.Invoke();
		}
	}

	private void AddNotification(ToastNotification notification)
	{
		lock (_lock)
		{
			// Limit active toast notifications
			while (_notifications.Count >= 10)
			{
				_notifications.RemoveAt(0);
			}
			_notifications.Add(notification);

			// Maintain history with max 50 entries
			while (_history.Count >= 50)
			{
				_history.RemoveAt(0);
			}
			_history.Add(notification);
			_unreadCount++;
		}
		OnChange?.Invoke();
	}
}

/// <summary>
/// Represents a toast notification
/// </summary>
public class ToastNotification
{
	public Guid Id { get; set; }
	public string Title { get; set; } = string.Empty;
	public string Message { get; set; } = string.Empty;
	public NotificationType Type { get; set; }
	public DateTime CreatedAt { get; set; }
	public int DurationMs { get; set; }
}

/// <summary>
/// Types of notifications
/// </summary>
public enum NotificationType
{
	Success,
	Error,
	Warning,
	Info
}
