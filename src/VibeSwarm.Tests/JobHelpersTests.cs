using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Services;
using VibeSwarm.Shared.Validation;
using VibeSwarm.Web.Controllers;
using VibeSwarm.Web.Services;

namespace VibeSwarm.Tests;

/// <summary>
/// A running job asks its project's Decisions and Research profiles through vibeswarm-ask,
/// which posts to <see cref="JobHelpersController"/> with the run's token.
/// </summary>
public sealed class JobHelpersTests : IDisposable
{
	private readonly SqliteConnection _connection;
	private readonly ServiceProvider _services;
	private readonly JobHelperSessions _sessions = new();
	private readonly FakeConsultService _consult = new();
	private readonly Guid _jobId = Guid.NewGuid();
	private readonly Guid _inferenceProviderId = Guid.NewGuid();

	public JobHelpersTests()
	{
		_connection = new SqliteConnection("Data Source=:memory:");
		_connection.Open();
		var collection = new ServiceCollection();
		collection.AddDbContext<VibeSwarmDbContext>(options => options.UseSqlite(_connection));
		_services = collection.BuildServiceProvider();
		using var scope = _services.CreateScope();
		scope.ServiceProvider.GetRequiredService<VibeSwarmDbContext>().Database.EnsureCreated();
	}

	[Fact]
	public async Task Ask_WithTheRunsToken_PutsTheQuestionToTheNamedHelper()
	{
		var session = Register();
		_consult.Answer = "Use the queue.";

		var result = await AskAsync(session.Token, "Research", "Which queue library is current?");

		Assert.Equal(StatusCodes.Status200OK, result.StatusCode);
		Assert.Equal("Use the queue.", result.Content);
		var request = Assert.Single(_consult.Requests);
		Assert.Equal("Which queue library is current?", request.Prompt);
		Assert.Equal(_inferenceProviderId, request.Target.InferenceProviderId);
		Assert.Equal(_jobId, request.JobId);
		// An inference helper sees only the question, never the checkout.
		Assert.Null(request.WorkingDirectory);
	}

	[Fact]
	public async Task Ask_WithAWrongTokenOrUnknownHelper_IsRefusedWithoutAsking()
	{
		var session = Register();

		var wrongToken = await AskAsync("not-the-token", "research", "Hi?");
		Assert.Equal(StatusCodes.Status401Unauthorized, wrongToken.StatusCode);

		var unknown = await AskAsync(session.Token, "oracle", "Hi?");
		Assert.Equal(StatusCodes.Status404NotFound, unknown.StatusCode);
		Assert.Contains("decisions, research", unknown.Content);

		var empty = await AskAsync(session.Token, "research", "   ");
		Assert.Equal(StatusCodes.Status400BadRequest, empty.StatusCode);

		var tooLong = await AskAsync(session.Token, "research", new string('a', ValidationLimits.JobHelperQuestionMaxLength + 1));
		Assert.Equal(StatusCodes.Status413PayloadTooLarge, tooLong.StatusCode);

		Assert.Empty(_consult.Requests);
	}

	[Fact]
	public async Task Ask_StopsOnceTheRunHasAskedItsQuestions()
	{
		var session = Register();
		for (var question = 0; question < ValidationLimits.JobHelperQuestionsPerRun; question++)
		{
			Assert.Equal(StatusCodes.Status200OK, (await AskAsync(session.Token, "decisions", $"Q{question}")).StatusCode);
		}

		var over = await AskAsync(session.Token, "decisions", "One more?");

		Assert.Equal(StatusCodes.Status429TooManyRequests, over.StatusCode);
		Assert.Equal(ValidationLimits.JobHelperQuestionsPerRun, _consult.Requests.Count);
	}

	[Fact]
	public async Task Ask_TakesOneQuestionAtATime_AndNothingAfterTheRunEnds()
	{
		var session = Register();
		var release = new TaskCompletionSource();
		_consult.Hold = release.Task;

		var first = AskAsync(session.Token, "decisions", "First?");
		var second = await AskAsync(session.Token, "decisions", "Second?");
		Assert.Equal(StatusCodes.Status409Conflict, second.StatusCode);

		release.SetResult();
		Assert.Equal(StatusCodes.Status200OK, (await first).StatusCode);

		_sessions.Remove(_jobId);
		Assert.Equal(StatusCodes.Status401Unauthorized, (await AskAsync(session.Token, "decisions", "Third?")).StatusCode);
	}

	[Fact]
	public void Command_PostsTheQuestionWithTheTokenAndHelper()
	{
		var script = JobHelperCommand.BuildScript("http://127.0.0.1:5000/api/jobs/x/helpers/ask", "secret", selfSigned: false);

		Assert.StartsWith("#!/bin/sh", script);
		Assert.Contains("X-VibeSwarm-Token: secret", script);
		Assert.Contains("X-VibeSwarm-Helper: $helper", script);
		Assert.Contains("--data-binary @-", script);
		Assert.DoesNotContain(" -k ", script);
		Assert.Contains(" -k ", JobHelperCommand.BuildScript("https://127.0.0.1:5001/x", "secret", selfSigned: true));
	}

	[Fact]
	public void WrittenCommand_IsValidShell_ReadableOnlyByItsOwner_AndRemovedAfter()
	{
		if (OperatingSystem.IsWindows())
		{
			return;
		}

		var jobId = Guid.NewGuid();
		var path = JobHelperCommand.WriteScript(jobId, "http://127.0.0.1:5000", "secret");
		try
		{
			Assert.Equal(JobHelperCommand.GetScriptPath(jobId), path);
			Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(path));
			using var check = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("sh", ["-n", path]) { RedirectStandardError = true })!;
			var errors = check.StandardError.ReadToEnd();
			check.WaitForExit();
			Assert.True(check.ExitCode == 0, errors);
			Assert.Contains($"http://127.0.0.1:5000/api/jobs/{jobId}/helpers/ask", File.ReadAllText(path));
		}
		finally
		{
			JobHelperCommand.DeleteScript(jobId);
		}

		Assert.False(File.Exists(path));
	}

	[Theory]
	[InlineData("http://0.0.0.0:5000", "http://127.0.0.1:5000")]
	[InlineData("http://[::]:5000", "http://127.0.0.1:5000")]
	[InlineData("http://+:5000", "http://127.0.0.1:5000")]
	[InlineData("https://localhost:5001/", "https://127.0.0.1:5001")]
	[InlineData("http://192.168.1.9:8080", "http://192.168.1.9:8080")]
	[InlineData("unix:/run/app.sock", null)]
	public void Loopback_PointsAtThisMachine(string address, string? expected)
		=> Assert.Equal(expected, JobHelperCommand.ToLoopback(address));

	[Fact]
	public void Prompt_NamesEachHelperAndWhatItCanSee()
	{
		var rules = PromptBuilder.BuildHelperRules(
			"/tmp/vibeswarm/helpers/x/vibeswarm-ask",
			[("decisions", "Second opinions.", "GPT: GitHub Copilot, gpt-5", true), ("research", "Research.", "Grok: Grok", false)],
			ValidationLimits.JobHelperQuestionsPerRun);

		Assert.NotNull(rules);
		Assert.StartsWith("<helpers>", rules);
		Assert.Contains("/tmp/vibeswarm/helpers/x/vibeswarm-ask <name> <<'EOF'", rules);
		Assert.Contains("- decisions (GPT: GitHub Copilot, gpt-5): Second opinions. It can read the repository.", rules);
		Assert.Contains("- research (Grok: Grok): Research. It sees only your question", rules);
		Assert.Null(PromptBuilder.BuildHelperRules("/x", [], 10));
	}

	private JobHelperSession Register()
	{
		var session = new JobHelperSession
		{
			JobId = _jobId,
			Token = JobHelperCommand.NewToken(),
			WorkingDirectory = "/tmp/checkout",
			Helpers =
			[
				new JobHelper("decisions", "Second opinions.", "GPT", "GitHub Copilot", "gpt-5",
					new ConsultTarget(Guid.NewGuid(), null, "gpt-5", null), ReadsRepository: true),
				new JobHelper("research", "Research.", "Grok", "Grok", null,
					new ConsultTarget(null, _inferenceProviderId, null, null), ReadsRepository: false),
			],
		};
		_sessions.Register(session);
		return session;
	}

	private async Task<ContentResult> AskAsync(string token, string helper, string question)
	{
		var controller = new JobHelpersController(_sessions, _consult, _services.GetRequiredService<IServiceScopeFactory>(), NullLogger<JobHelpersController>.Instance)
		{
			ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
		};
		var request = controller.ControllerContext.HttpContext.Request;
		request.Headers[JobHelperCommand.TokenHeader] = token;
		request.Headers[JobHelperCommand.HelperHeader] = helper;
		request.Body = new MemoryStream(Encoding.UTF8.GetBytes(question));

		return Assert.IsType<ContentResult>(await controller.Ask(_jobId, CancellationToken.None));
	}

	public void Dispose()
	{
		_services.Dispose();
		_connection.Dispose();
	}

	private sealed class FakeConsultService : IReadOnlyConsultService
	{
		public List<ConsultRequest> Requests { get; } = [];
		public string Answer { get; set; } = "An answer.";
		public Task? Hold { get; set; }

		public async Task<ConsultResult> ConsultAsync(ConsultRequest request, CancellationToken cancellationToken = default)
		{
			Requests.Add(request);
			if (Hold != null)
			{
				var hold = Hold;
				Hold = null;
				await hold.WaitAsync(cancellationToken);
			}

			return new ConsultResult { Success = true, Text = Answer };
		}
	}
}
