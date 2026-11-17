namespace VibeSwarm.Web.Services;

/// <summary>
/// Messages the user sends a running job to steer its agent. A message stops the agent's current
/// turn, and the run carries on in the same session with it. Every message goes into exactly one
/// prompt: those sent between turns join the next turn's prompt, those sent during a turn stop it.
/// The inbox takes messages only while the agent is working, so one sent as the run wraps up is
/// refused instead of silently dropped.
/// </summary>
internal sealed class JobMessageInbox
{
	private readonly object _lock = new();
	private readonly List<string> _pending = [];
	private CancellationTokenSource? _turn;
	private bool _isOpen;

	public void Open()
	{
		lock (_lock)
		{
			_isOpen = true;
		}
	}

	/// <summary>Queues a message and stops the agent's turn, or returns false when the run no longer takes messages.</summary>
	public bool TryPost(string message)
	{
		lock (_lock)
		{
			if (!_isOpen)
			{
				return false;
			}

			_pending.Add(message);
			try
			{
				// Cancellation callbacks (killing the agent's process) run on the thread pool, not the sender's.
				_ = _turn?.CancelAsync();
			}
			catch (ObjectDisposedException)
			{
				// The turn already ended; the message waits for the next one.
			}

			return true;
		}
	}

	/// <summary>
	/// Starts an agent turn that the next message stops. Returns the messages that arrived since
	/// the last turn; they belong in this turn's prompt.
	/// </summary>
	public IReadOnlyList<string> BeginTurn(CancellationTokenSource turn)
	{
		lock (_lock)
		{
			_turn = turn;
			return TakeLocked();
		}
	}

	public void EndTurn(CancellationTokenSource turn)
	{
		lock (_lock)
		{
			if (ReferenceEquals(_turn, turn))
			{
				_turn = null;
			}
		}
	}

	public bool HasPending
	{
		get
		{
			lock (_lock)
			{
				return _pending.Count > 0;
			}
		}
	}

	/// <summary>Takes the waiting messages, oldest first.</summary>
	public IReadOnlyList<string> Take()
	{
		lock (_lock)
		{
			return TakeLocked();
		}
	}

	/// <summary>
	/// Takes the waiting messages or, when there are none, closes the inbox. Both happen under one
	/// lock, so no message can arrive after the run decides it is done.
	/// </summary>
	public IReadOnlyList<string> TakeOrClose()
	{
		lock (_lock)
		{
			var messages = TakeLocked();
			if (messages.Count == 0)
			{
				CloseLocked();
			}

			return messages;
		}
	}

	public void Close()
	{
		lock (_lock)
		{
			CloseLocked();
		}
	}

	private List<string> TakeLocked()
	{
		var messages = _pending.ToList();
		_pending.Clear();
		return messages;
	}

	private void CloseLocked()
	{
		_isOpen = false;
		_turn = null;
		_pending.Clear();
	}
}
