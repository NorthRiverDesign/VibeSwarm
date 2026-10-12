using VibeSwarm.Shared.Data;

namespace VibeSwarm.Shared.Inference;

/// <summary>
/// Which inference model handles a task: the model a provider assigns to that task, or else its
/// default model. Server and client resolve it the same way, so Settings names the model that
/// will actually answer.
/// </summary>
public static class InferenceTaskModels
{
	public const string Default = "default";

	/// <summary>Answering a running agent's question while the user is away.</summary>
	public const string AgentAnswer = "agent-answer";

	public static InferenceModel? Resolve(InferenceProvider provider, string taskType)
	{
		return provider.Models
			.Where(m => m.IsAvailable && m.IsDefault && string.Equals(m.TaskType, taskType, StringComparison.OrdinalIgnoreCase))
			.OrderBy(m => m.ModelId, StringComparer.Ordinal)
			.FirstOrDefault()
			?? provider.Models
				.Where(m => m.IsAvailable && m.IsDefault && string.Equals(m.TaskType, Default, StringComparison.OrdinalIgnoreCase))
				.OrderBy(m => m.ModelId, StringComparer.Ordinal)
				.FirstOrDefault();
	}

	/// <summary>
	/// The enabled providers that can handle <paramref name="taskType"/>, in the order they are
	/// tried, each with its model.
	/// </summary>
	public static IReadOnlyList<(InferenceProvider Provider, InferenceModel Model)> Candidates(
		IEnumerable<InferenceProvider> providers,
		string taskType)
	{
		return providers
			.Where(p => p.IsEnabled)
			.OrderBy(p => p.Priority)
			.ThenBy(p => p.Name, StringComparer.Ordinal)
			.Select(p => (Provider: p, Model: Resolve(p, taskType)))
			.Where(c => c.Model != null)
			.Select(c => (c.Provider, c.Model!))
			.ToList();
	}
}
