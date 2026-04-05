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
{"model":"selected-model","choices":[{"message":{"content":"Hello"}}],"usage":{"prompt_tokens":2,"completion_tokens":1}}
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
	}

	private sealed class FakeHttpClientFactory(HttpClient httpClient) : IHttpClientFactory
	{
		public HttpClient CreateClient(string name) => httpClient;
	}

	private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
	{
		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
			=> handler(request, cancellationToken);
	}

	private sealed class FakeInferenceProviderService(IReadOnlyList<InferenceProvider> providers) : IInferenceProviderService
	{
		public Task<IEnumerable<InferenceProvider>> GetAllAsync(CancellationToken ct = default)
			=> Task.FromResult<IEnumerable<InferenceProvider>>(providers);

		public Task<InferenceProvider?> GetByIdAsync(Guid id, CancellationToken ct = default)
			=> Task.FromResult(providers.FirstOrDefault(provider => provider.Id == id));

		public Task<IEnumerable<InferenceProvider>> GetEnabledAsync(CancellationToken ct = default)
			=> Task.FromResult<IEnumerable<InferenceProvider>>(providers.Where(provider => provider.IsEnabled).ToList());

		public Task<InferenceProvider> CreateAsync(InferenceProvider provider, CancellationToken ct = default)
			=> throw new NotSupportedException();

		public Task<InferenceProvider> UpdateAsync(InferenceProvider provider, CancellationToken ct = default)
			=> throw new NotSupportedException();

		public Task DeleteAsync(Guid id, CancellationToken ct = default)
			=> throw new NotSupportedException();

		public Task<IEnumerable<InferenceModel>> GetModelsAsync(Guid providerId, CancellationToken ct = default)
			=> Task.FromResult<IEnumerable<InferenceModel>>([]);

		public Task<IEnumerable<InferenceModel>> RefreshModelsAsync(Guid providerId, CancellationToken ct = default)
			=> Task.FromResult<IEnumerable<InferenceModel>>([]);

		public Task SetModelForTaskAsync(Guid providerId, string modelId, string taskType, CancellationToken ct = default)
			=> throw new NotSupportedException();

		public Task<InferenceModel?> GetModelForTaskAsync(string taskType, CancellationToken ct = default)
			=> Task.FromResult<InferenceModel?>(null);
	}
}
