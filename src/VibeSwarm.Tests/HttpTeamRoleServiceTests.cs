using System.Net;
using System.Text;
using System.Text.Json;
using VibeSwarm.Client.Services;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Providers;

namespace VibeSwarm.Tests;

public sealed class HttpTeamRoleServiceTests
{
	[Fact]
	public async Task CreateAsync_SendsScalarOnlyPayload()
	{
		var skillId = Guid.NewGuid();
		TeamRole? capturedPayload = null;
		var service = CreateService(async request =>
		{
			capturedPayload = await DeserializeRequestAsync(request);
			return CreateJsonResponse(HttpStatusCode.OK, new TeamRole
			{
				Id = Guid.NewGuid(),
				Name = capturedPayload!.Name,
				SkillLinks = capturedPayload.SkillLinks
			});
		});

		await service.CreateAsync(new TeamRole
		{
			Name = "Security Reviewer",
			DefaultProviderId = Guid.NewGuid(),
			DefaultReasoningEffort = "high",
			DefaultProvider = new Provider
			{
				Id = Guid.NewGuid(),
				Name = "Claude Code",
				Type = ProviderType.Claude,
				ConnectionMode = ProviderConnectionMode.CLI
			},
			SkillLinks =
			[
				new TeamRoleSkill
				{
					SkillId = skillId,
					Skill = new Skill
					{
						Id = skillId,
						Name = "secure-review",
						Content = "Review auth boundaries."
					}
				},
				new TeamRoleSkill
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
		Assert.Single(capturedPayload.SkillLinks);
		Assert.All(capturedPayload.SkillLinks, link => Assert.Null(link.Skill));
	}

	[Fact]
	public async Task UpdateAsync_SendsScalarOnlyPayload()
	{
		var teamRoleId = Guid.NewGuid();
		var skillId = Guid.NewGuid();
		TeamRole? capturedPayload = null;
		var service = CreateService(async request =>
		{
			capturedPayload = await DeserializeRequestAsync(request);
			return CreateJsonResponse(HttpStatusCode.OK, new TeamRole
			{
				Id = teamRoleId,
				Name = capturedPayload!.Name,
				SkillLinks = capturedPayload.SkillLinks
			});
		});

		await service.UpdateAsync(new TeamRole
		{
			Id = teamRoleId,
			Name = "Platform Engineer",
			DefaultReasoningEffort = "medium",
			SkillLinks =
			[
				new TeamRoleSkill
				{
					TeamRoleId = teamRoleId,
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
		Assert.Equal(teamRoleId, capturedPayload!.Id);
		Assert.Equal("medium", capturedPayload.DefaultReasoningEffort);
		Assert.Single(capturedPayload.SkillLinks);
		Assert.Equal(skillId, capturedPayload.SkillLinks.Single().SkillId);
		Assert.All(capturedPayload.SkillLinks, link => Assert.Null(link.Skill));
	}

	private static HttpTeamRoleService CreateService(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler)
	{
		var httpClient = new HttpClient(new StubHttpMessageHandler((request, _) => handler(request)))
		{
			BaseAddress = new Uri("https://example.test")
		};

		return new HttpTeamRoleService(httpClient);
	}

	private static async Task<TeamRole> DeserializeRequestAsync(HttpRequestMessage request)
	{
		var json = await request.Content!.ReadAsStringAsync();
		return JsonSerializer.Deserialize<TeamRole>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))
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
