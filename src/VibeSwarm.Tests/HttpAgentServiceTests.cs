using System.Net;
using System.Text;
using System.Text.Json;
using VibeSwarm.Client.Services;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Providers;

namespace VibeSwarm.Tests;

public sealed class HttpAgentServiceTests
{
	[Fact]
	public async Task CreateAsync_SendsScalarOnlyPayload()
	{
		var skillId = Guid.NewGuid();
		Agent? capturedPayload = null;
		var service = CreateService(async request =>
		{
			capturedPayload = await DeserializeRequestAsync(request);
			return CreateJsonResponse(HttpStatusCode.OK, new Agent
			{
				Id = Guid.NewGuid(),
				Name = capturedPayload!.Name,
				SkillLinks = capturedPayload.SkillLinks
			});
		});

		await service.CreateAsync(new Agent
		{
			Name = "Security Reviewer",
			DefaultProviderId = Guid.NewGuid(),
			DefaultReasoningEffort = "high",
			DefaultCycleMode = CycleMode.Autonomous,
			DefaultCycleSessionMode = CycleSessionMode.ContinueSession,
			DefaultMaxCycles = 5,
			DefaultCycleReviewPrompt = "Review the last cycle before deciding whether to continue.",
			DefaultProvider = new Provider
			{
				Id = Guid.NewGuid(),
				Name = "Claude Code",
				Type = ProviderType.Claude,
				ConnectionMode = ProviderConnectionMode.CLI
			},
			SkillLinks =
			[
				new AgentSkill
				{
					SkillId = skillId,
					Skill = new Skill
					{
						Id = skillId,
						Name = "secure-review",
						Content = "Review auth boundaries."
					}
				},
				new AgentSkill
				{
					SkillId = skillId,
					Skill = new Skill
					{
						Id = skillId,
						Name = "secure-review",
						Content = "Review auth boundaries."
					}
				}
			]
		});

		Assert.NotNull(capturedPayload);
		Assert.Null(capturedPayload!.DefaultProvider);
		Assert.Equal("high", capturedPayload.DefaultReasoningEffort);
		Assert.Equal(CycleMode.Autonomous, capturedPayload.DefaultCycleMode);
		Assert.Equal(CycleSessionMode.ContinueSession, capturedPayload.DefaultCycleSessionMode);
		Assert.Equal(5, capturedPayload.DefaultMaxCycles);
		Assert.Equal("Review the last cycle before deciding whether to continue.", capturedPayload.DefaultCycleReviewPrompt);
		Assert.Single(capturedPayload.SkillLinks);
		Assert.All(capturedPayload.SkillLinks, link => Assert.Null(link.Skill));
	}

	[Fact]
	public async Task UpdateAsync_SendsScalarOnlyPayload()
	{
		var agentId = Guid.NewGuid();
		var skillId = Guid.NewGuid();
		Agent? capturedPayload = null;
		var service = CreateService(async request =>
		{
			capturedPayload = await DeserializeRequestAsync(request);
			return CreateJsonResponse(HttpStatusCode.OK, new Agent
			{
				Id = agentId,
				Name = capturedPayload!.Name,
				SkillLinks = capturedPayload.SkillLinks
			});
		});

		await service.UpdateAsync(new Agent
		{
			Id = agentId,
			Name = "Platform Engineer",
			DefaultReasoningEffort = "medium",
			DefaultCycleMode = CycleMode.FixedCount,
			DefaultCycleSessionMode = CycleSessionMode.FreshSession,
			DefaultMaxCycles = 3,
			DefaultCycleReviewPrompt = "Check whether the goal is complete after each pass.",
			SkillLinks =
			[
				new AgentSkill
				{
					AgentId = agentId,
					SkillId = skillId,
					Skill = new Skill
					{
						Id = skillId,
						Name = "deployment",
						Content = "Ship safely."
					}
				}
			]
		});

		Assert.NotNull(capturedPayload);
		Assert.Equal(agentId, capturedPayload!.Id);
		Assert.Equal("medium", capturedPayload.DefaultReasoningEffort);
		Assert.Equal(CycleMode.FixedCount, capturedPayload.DefaultCycleMode);
		Assert.Equal(CycleSessionMode.FreshSession, capturedPayload.DefaultCycleSessionMode);
		Assert.Equal(3, capturedPayload.DefaultMaxCycles);
		Assert.Equal("Check whether the goal is complete after each pass.", capturedPayload.DefaultCycleReviewPrompt);
		Assert.Single(capturedPayload.SkillLinks);
		Assert.Equal(skillId, capturedPayload.SkillLinks.Single().SkillId);
		Assert.All(capturedPayload.SkillLinks, link => Assert.Null(link.Skill));
	}

	private static HttpAgentService CreateService(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler)
	{
		var httpClient = new HttpClient(new StubHttpMessageHandler((request, _) => handler(request)))
		{
			BaseAddress = new Uri("https://example.test")
		};

		return new HttpAgentService(httpClient);
	}

	private static async Task<Agent> DeserializeRequestAsync(HttpRequestMessage request)
	{
		var json = await request.Content!.ReadAsStringAsync();
		return JsonSerializer.Deserialize<Agent>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))
			?? throw new InvalidOperationException("Failed to deserialize team role request payload.");
	}

	private static HttpResponseMessage CreateJsonResponse(HttpStatusCode statusCode, object payload)
	{
		return new HttpResponseMessage(statusCode)
		{
			Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
		};
	}

	private sealed class StubHttpMessageHandler(
		Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
	{
		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
			=> handler(request, cancellationToken);
	}
}
