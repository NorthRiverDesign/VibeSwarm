using System.ComponentModel.DataAnnotations;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Validation;

namespace VibeSwarm.Tests;

/// <summary>
/// The API validates posted jobs and templates with these attributes, so they decide how long a
/// custom job's goal prompt may be.
/// </summary>
public sealed class JobGoalPromptLimitTests
{
	[Theory]
	[InlineData(2001, true)]
	[InlineData(ValidationLimits.JobGoalPromptMaxLength, true)]
	[InlineData(ValidationLimits.JobGoalPromptMaxLength + 1, false)]
	public void JobGoalPrompt_AllowsPromptsUpToTheLimit(int length, bool expectedValid)
	{
		var job = new Job { GoalPrompt = new string('a', length) };

		Assert.Equal(expectedValid, IsValid(job, nameof(Job.GoalPrompt), job.GoalPrompt));
	}

	[Theory]
	[InlineData(2001, true)]
	[InlineData(ValidationLimits.JobGoalPromptMaxLength + 1, false)]
	public void JobTemplateGoalPrompt_MatchesTheJobLimit(int length, bool expectedValid)
	{
		var template = new JobTemplate { Name = "Long", GoalPrompt = new string('a', length) };

		Assert.Equal(expectedValid, IsValid(template, nameof(JobTemplate.GoalPrompt), template.GoalPrompt));
	}

	private static bool IsValid(object instance, string memberName, object? value)
	{
		return Validator.TryValidateProperty(value, new ValidationContext(instance) { MemberName = memberName }, []);
	}
}
