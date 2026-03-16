using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Services;

namespace VibeSwarm.Tests;

public sealed class JobCheckpointStateMachineTests
{
	[Fact]
	public void TryTransition_AllowsPreservationLifecycle()
	{
		var job = new Job
		{
			GoalPrompt = "Protect local changes"
		};

		Assert.True(JobCheckpointStateMachine.TryTransition(job, GitCheckpointStatus.Protecting));
		Assert.True(JobCheckpointStateMachine.TryTransition(job, GitCheckpointStatus.Preserved));
		Assert.NotNull(job.GitCheckpointCapturedAt);
		Assert.True(JobCheckpointStateMachine.TryTransition(job, GitCheckpointStatus.Cleared));
		Assert.Equal(GitCheckpointStatus.Cleared, job.GitCheckpointStatus);
	}

	[Fact]
	public void TryTransition_RejectsInvalidTransition()
	{
		var job = new Job
		{
			GoalPrompt = "Protect local changes",
			GitCheckpointStatus = GitCheckpointStatus.None
		};

		Assert.False(JobCheckpointStateMachine.TryTransition(job, GitCheckpointStatus.Cleared));
		Assert.Equal(GitCheckpointStatus.None, job.GitCheckpointStatus);
	}
}
