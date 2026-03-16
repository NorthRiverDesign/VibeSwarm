using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using VibeSwarm.Web.Hubs;
using VibeSwarm.Web.Services;

namespace VibeSwarm.Tests;

public sealed class SignalRJobUpdateServiceTests
{
	[Fact]
	public async Task NotifyJobCompleted_BroadcastsToGlobalEvents()
	{
		var hubContext = new TestHubContext();
		var service = new SignalRJobUpdateService(hubContext, NullLogger<SignalRJobUpdateService>.Instance);
		var jobId = Guid.NewGuid();

		await service.NotifyJobCompleted(jobId, success: true);

		AssertInvocation(
			hubContext,
			"group:global-events",
			"JobCompleted",
			jobId.ToString(),
			true,
			null);
	}

	[Fact]
	public async Task NotifyIdeaStarted_BroadcastsToGlobalEvents()
	{
		var hubContext = new TestHubContext();
		var service = new SignalRJobUpdateService(hubContext, NullLogger<SignalRJobUpdateService>.Instance);
		var ideaId = Guid.NewGuid();
		var projectId = Guid.NewGuid();
		var jobId = Guid.NewGuid();

		await service.NotifyIdeaStarted(ideaId, projectId, jobId);

		AssertInvocation(
			hubContext,
			"group:global-events",
			"IdeaStarted",
			ideaId.ToString(),
			projectId.ToString(),
			jobId.ToString());
	}

	private static void AssertInvocation(
		TestHubContext hubContext,
		string target,
		string method,
		params object?[] expectedArgs)
	{
		var invocation = Assert.Single(hubContext.Invocations, i => i.Target == target && i.Method == method);
		Assert.Equal(expectedArgs, invocation.Args);
	}

	private sealed class TestHubContext : IHubContext<JobHub>
	{
		private readonly TestHubClients _clients;

		public TestHubContext()
		{
			_clients = new TestHubClients(this);
		}

		public List<InvocationRecord> Invocations { get; } = new();

		public IHubClients Clients => _clients;

		public IGroupManager Groups { get; } = new TestGroupManager();
	}

	private sealed class TestHubClients : IHubClients
	{
		private readonly TestHubContext _context;

		public TestHubClients(TestHubContext context)
		{
			_context = context;
		}

		public IClientProxy All => new TestClientProxy(_context, "all");

		public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => new TestClientProxy(_context, "all-except");

		public IClientProxy Client(string connectionId) => new TestClientProxy(_context, $"client:{connectionId}");

		public IClientProxy Clients(IReadOnlyList<string> connectionIds) => new TestClientProxy(_context, "clients");

		public IClientProxy Group(string groupName) => new TestClientProxy(_context, $"group:{groupName}");

		public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => new TestClientProxy(_context, $"group-except:{groupName}");

		public IClientProxy Groups(IReadOnlyList<string> groupNames) => new TestClientProxy(_context, $"groups:{string.Join(",", groupNames)}");

		public IClientProxy User(string userId) => new TestClientProxy(_context, $"user:{userId}");

		public IClientProxy Users(IReadOnlyList<string> userIds) => new TestClientProxy(_context, "users");
	}

	private sealed class TestClientProxy : IClientProxy
	{
		private readonly TestHubContext _context;
		private readonly string _target;

		public TestClientProxy(TestHubContext context, string target)
		{
			_context = context;
			_target = target;
		}

		public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default)
		{
			_context.Invocations.Add(new InvocationRecord(_target, method, args));
			return Task.CompletedTask;
		}
	}

	private sealed class TestGroupManager : IGroupManager
	{
		public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
			=> Task.CompletedTask;

		public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
			=> Task.CompletedTask;
	}

	private sealed record InvocationRecord(string Target, string Method, object?[] Args);
}
