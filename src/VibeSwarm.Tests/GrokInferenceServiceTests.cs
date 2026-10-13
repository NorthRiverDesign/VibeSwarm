using System.Net;
using System.Text;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Inference;
using VibeSwarm.Shared.Services;
using VibeSwarm.Web.Services;

namespace VibeSwarm.Tests;

public sealed class GrokInferenceServiceTests
{
	[Fact]
	public async Task GenerateAsync_UsesSelectedProviderApiKeyAndDefaultModel_WhenProviderIdSpecified()
	{
		var otherProvider = new InferenceProvider
		{
			Id = Guid.NewGuid(),
			Name = "Other Grok",
			ProviderType = InferenceProviderType.Grok,
			Endpoint = "https://other.x.ai/v1",
			ApiKey = "other-key",
			IsEnabled = true,
			Models =
			[
				new InferenceModel
				{
					InferenceProviderId = Guid.NewGuid(),
					ModelId = "other-model",
					IsAvailable = true,
					IsDefault = true,
					TaskType = "default"
				}
			]
		};
		var selectedProvider = new InferenceProvider
		{
			Id = Guid.NewGuid(),
			Name = "Selected Grok",
			ProviderType = InferenceProviderType.Grok,
			Endpoint = "https://selected.x.ai/v1",
			ApiKey = "selected-key",
			IsEnabled = true,
			Models =
			[
				new InferenceModel
				{
					InferenceProviderId = Guid.NewGuid(),
					ModelId = "selected-model",
					IsAvailable = true,
					IsDefault = true,
					TaskType = "default"
				}
			]
		};
		Uri? requestUri = null;
		string? requestBody = null;
		string? apiKey = null;
		var handler = new StubHttpMessageHandler(async (request, _) =>
		{
			requestUri = request.RequestUri;
			apiKey = request.Headers.Authorization?.Parameter;
			requestBody = await request.Content!.ReadAsStringAsync();
			return new HttpResponseMessage(HttpStatusCode.OK)
			{
				Content = new StringContent("""
{"model":"selected-model","choices":[{"message":{"content":"Hello"},"finish_reason":"stop"}],"usage":{"prompt_tokens":2,"completion_tokens":1,"cost_in_usd_ticks":37756000}}
""", Encoding.UTF8, "application/json")
			};
		});
		var httpClient = new HttpClient(handler);
		var service = new GrokInferenceService(
			new FakeHttpClientFactory(httpClient),
			new FakeInferenceProviderService([otherProvider, selectedProvider]));

		var response = await service.GenerateAsync(new InferenceRequest
		{
			ProviderId = selectedProvider.Id,
			Endpoint = selectedProvider.Endpoint,
			Prompt = "Say hello."
		});

		Assert.True(response.Success);
		Assert.Equal("selected-model", response.ModelUsed);
		Assert.Equal("selected-key", apiKey);
		Assert.Equal("https://selected.x.ai/v1/chat/completions", requestUri?.ToString());
		Assert.Contains("\"model\":\"selected-model\"", requestBody, StringComparison.Ordinal);
		Assert.Contains("\"max_completion_tokens\":4096", requestBody, StringComparison.Ordinal);
		Assert.DoesNotContain("\"max_tokens\"", requestBody, StringComparison.Ordinal);
		Assert.Equal("stop", response.FinishReason);
		Assert.Equal(2, response.PromptTokens);
		Assert.Equal(1, response.CompletionTokens);
		Assert.Equal(0.0037756m, response.CostUsd);
	}

	[Fact]
	public async Task GetBalanceAsync_ReadsTeamFromApiKeyAndPrepaidBalanceWithManagementKey()
	{
		var provider = CreateGrokProvider(managementApiKey: "management-key");
		var requests = new List<(string Uri, string? Key)>();
		var handler = new StubHttpMessageHandler((request, _) =>
		{
			var uri = request.RequestUri!.ToString();
			requests.Add((uri, request.Headers.Authorization?.Parameter));
			var body = uri.EndsWith("/api-key", StringComparison.Ordinal)
				? """{"team_id":"team-1","name":"My API Key"}"""
				: """
{"changes":[
{"teamId":"team-1","changeOrigin":"PURCHASE","amount":{"val":"-2500"}},
{"teamId":"team-1","changeOrigin":"SPEND","amount":{"val":"1266"}}
],"total":{"val":"-1234"}}
""";
			return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
			{
				Content = new StringContent(body, Encoding.UTF8, "application/json")
			});
		});
		var service = new GrokInferenceService(
			new FakeHttpClientFactory(new HttpClient(handler)),
			new FakeInferenceProviderService([provider]));

		var balance = await service.GetBalanceAsync(provider.Id);

		Assert.True(balance.IsSupported);
		Assert.Null(balance.Error);
		Assert.Equal(12.34m, balance.RemainingUsd);
		Assert.Equal(25m, balance.AddedUsd);
		Assert.Equal(12.66m, balance.SpentUsd);
		Assert.Equal(
			[
				("https://api.x.ai/v1/api-key", "api-key"),
				("https://management-api.x.ai/v1/billing/teams/team-1/prepaid/balance", "management-key")
			],
			requests);
	}

	[Fact]
	public async Task GetBalanceAsync_WithoutManagementKey_ReportsNoBalanceWithoutCallingXai()
	{
		var provider = CreateGrokProvider(managementApiKey: null);
		var calls = 0;
		var handler = new StubHttpMessageHandler((_, _) =>
		{
			calls++;
			return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
		});
		var service = new GrokInferenceService(
			new FakeHttpClientFactory(new HttpClient(handler)),
			new FakeInferenceProviderService([provider]));

		var balance = await service.GetBalanceAsync(provider.Id);

		Assert.False(balance.IsSupported);
		Assert.Null(balance.RemainingUsd);
		Assert.Contains("management key", balance.Error, StringComparison.Ordinal);
		Assert.Equal(0, calls);
	}

	[Fact]
	public async Task GetBalanceAsync_RejectedManagementKey_ReportsTheRejection()
	{
		var provider = CreateGrokProvider(managementApiKey: "wrong-key");
		var handler = new StubHttpMessageHandler((request, _) => Task.FromResult(
			request.RequestUri!.ToString().EndsWith("/api-key", StringComparison.Ordinal)
				? new HttpResponseMessage(HttpStatusCode.OK)
				{
					Content = new StringContent("""{"team_id":"team-1"}""", Encoding.UTF8, "application/json")
				}
				: new HttpResponseMessage(HttpStatusCode.Forbidden)));
		var service = new GrokInferenceService(
			new FakeHttpClientFactory(new HttpClient(handler)),
			new FakeInferenceProviderService([provider]));

		var balance = await service.GetBalanceAsync(provider.Id);

		Assert.True(balance.IsSupported);
		Assert.Null(balance.RemainingUsd);
		Assert.Equal("X.AI rejected the management key (403).", balance.Error);
	}

	private static InferenceProvider CreateGrokProvider(string? managementApiKey) => new()
	{
		Id = Guid.NewGuid(),
		Name = "Grok",
		ProviderType = InferenceProviderType.Grok,
		Endpoint = "https://api.x.ai/v1/",
		ApiKey = "api-key",
		ManagementApiKey = managementApiKey,
		IsEnabled = true
	};

	private sealed class FakeHttpClientFactory(HttpClient httpClient) : IHttpClientFactory
	{
		public HttpClient CreateClient(string name) => httpClient;
	}

	private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
	{
		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
			=> handler(request, cancellationToken);
	}

	private sealed class FakeInferenceProviderService(IReadOnlyList<InferenceProvider> providers) : FakeInferenceProviderServiceBase
	{
		public override Task<IEnumerable<InferenceProvider>> GetAllAsync(CancellationToken ct = default)
			=> Task.FromResult<IEnumerable<InferenceProvider>>(providers);

		public override Task<InferenceProvider?> GetByIdAsync(Guid id, CancellationToken ct = default)
			=> Task.FromResult(providers.FirstOrDefault(provider => provider.Id == id));

		public override Task<IEnumerable<InferenceProvider>> GetEnabledAsync(CancellationToken ct = default)
			=> Task.FromResult<IEnumerable<InferenceProvider>>(providers.Where(provider => provider.IsEnabled).ToList());

		public override Task<InferenceProvider> CreateAsync(InferenceProvider provider, CancellationToken ct = default)
			=> throw new NotSupportedException();

		public override Task<InferenceProvider> UpdateAsync(InferenceProvider provider, CancellationToken ct = default)
			=> throw new NotSupportedException();

		public override Task DeleteAsync(Guid id, CancellationToken ct = default)
			=> throw new NotSupportedException();

		public override Task<IEnumerable<InferenceModel>> GetModelsAsync(Guid providerId, CancellationToken ct = default)
			=> Task.FromResult<IEnumerable<InferenceModel>>([]);

		public override Task<IEnumerable<InferenceModel>> RefreshModelsAsync(Guid providerId, CancellationToken ct = default)
			=> Task.FromResult<IEnumerable<InferenceModel>>([]);

		public override Task SetModelForTaskAsync(Guid providerId, string modelId, string taskType, CancellationToken ct = default)
			=> throw new NotSupportedException();

		public override Task<InferenceModel?> GetModelForTaskAsync(string taskType, CancellationToken ct = default)
			=> Task.FromResult<InferenceModel?>(null);
	}
}
