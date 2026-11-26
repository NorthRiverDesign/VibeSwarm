using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Services;
using VibeSwarm.Shared.Validation;

namespace VibeSwarm.Web.Services;

/// <summary>
/// A run's helpers: the project's Decisions and Research profiles, which the agent asks through
/// the <c>vibeswarm-ask</c> command while it works, so a large model can run the job and lean
/// on others for decisions and research.
/// </summary>
public partial class JobProcessingService
{
	private const string DecisionsPurpose = "A second opinion before a design or approach decision you're unsure of.";
	private const string ResearchPurpose = "Research: libraries, APIs, current facts and how others solve a problem.";

	/// <summary>
	/// Sets up the run's helpers and returns the prompt section naming them, or null when the
	/// project has none, or VibeSwarm can't be reached from the run.
	/// </summary>
	private async Task<string?> PrepareJobHelpersAsync(Job job, string? workingDirectory, VibeSwarmDbContext dbContext, CancellationToken cancellationToken)
	{
		var project = job.Project;
		if (project == null || (project.DecisionsProfileId == null && project.ResearchProfileId == null))
		{
			return null;
		}

		using var scope = _scopeFactory.CreateScope();
		var sessions = scope.ServiceProvider.GetService<JobHelperSessions>();
		var server = scope.ServiceProvider.GetService<IServer>();
		var baseUrl = JobHelperCommand.FindLoopbackBaseUrl(server);
		if (sessions == null || baseUrl == null || !JobHelperCommand.IsCurlAvailable())
		{
			_logger.LogInformation("Job {JobId} runs without its helpers: VibeSwarm's address or curl is not available", job.Id);
			return null;
		}

		var ids = new[] { project.DecisionsProfileId, project.ResearchProfileId }.OfType<Guid>().ToList();
		var profiles = await dbContext.Profiles
			.AsNoTracking()
			.Include(profile => profile.Provider)
			.Include(profile => profile.InferenceProvider)
			.Where(profile => ids.Contains(profile.Id))
			.ToDictionaryAsync(profile => profile.Id, cancellationToken);

		var helpers = new List<JobHelper>();
		AddHelper("decisions", DecisionsPurpose, project.DecisionsProfileId);
		AddHelper("research", ResearchPurpose, project.ResearchProfileId);
		if (helpers.Count == 0)
		{
			return null;
		}

		var token = JobHelperCommand.NewToken();
		string command;
		try
		{
			command = JobHelperCommand.WriteScript(job.Id, baseUrl, token);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			_logger.LogWarning(ex, "Could not write the helper command for job {JobId}; it runs without helpers", job.Id);
			return null;
		}

		sessions.Register(new JobHelperSession
		{
			JobId = job.Id,
			Token = token,
			Helpers = helpers,
			WorkingDirectory = workingDirectory,
		});

		return PromptBuilder.BuildHelperRules(
			command,
			helpers.Select(helper => (helper.Name, helper.Purpose, DescribeHelper(helper), helper.ReadsRepository)).ToList(),
			ValidationLimits.JobHelperQuestionsPerRun);

		void AddHelper(string name, string purpose, Guid? profileId)
		{
			if (profileId is not Guid id || !profiles.TryGetValue(id, out var profile) || !ProfileLinks.CanHelpJobs(profile))
			{
				return;
			}

			var enabled = profile.Provider?.IsEnabled ?? profile.InferenceProvider?.IsEnabled ?? false;
			if (!enabled)
			{
				return;
			}

			helpers.Add(new JobHelper(
				name,
				purpose,
				profile.Name,
				profile.Provider?.Name ?? profile.InferenceProvider!.Name,
				profile.ModelId,
				new ConsultTarget(profile.ProviderId, profile.InferenceProviderId, profile.ModelId, profile.ReasoningEffort),
				ReadsRepository: profile.ProviderId.HasValue));
		}
	}

	private static string DescribeHelper(JobHelper helper)
		=> string.IsNullOrWhiteSpace(helper.ModelId)
			? $"{helper.ProfileName}: {helper.ProviderName}"
			: $"{helper.ProfileName}: {helper.ProviderName}, {helper.ModelId}";

	/// <summary>Ends the run's helper session, stopping any question still out, and removes its command.</summary>
	private void EndJobHelpers(Guid jobId)
	{
		try
		{
			using var scope = _scopeFactory.CreateScope();
			scope.ServiceProvider.GetService<JobHelperSessions>()?.Remove(jobId);
		}
		catch (ObjectDisposedException)
		{
		}

		JobHelperCommand.DeleteScript(jobId);
	}
}
