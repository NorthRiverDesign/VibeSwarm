using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Models;
using VibeSwarm.Shared.Services;
using VibeSwarm.Web.Middleware;

namespace VibeSwarm.Tests;

public sealed class ExceptionHandlingMiddlewareTests
{
	[Fact]
	public async Task InvokeAsync_ReturnsJsonError_WhenCriticalErrorLoggingFails()
	{
		var middleware = new ExceptionHandlingMiddleware(
			_ => throw new InvalidOperationException("Boom"),
			NullLogger<ExceptionHandlingMiddleware>.Instance,
			new StubWebHostEnvironment());

		var context = new DefaultHttpContext();
		context.TraceIdentifier = "trace-123";
		context.Request.Method = HttpMethods.Get;
		context.Request.Path = "/api/test";
		context.Response.Body = new MemoryStream();

		await middleware.InvokeAsync(context, new ThrowingCriticalErrorLogService());

		Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
		Assert.Equal("application/json", context.Response.ContentType);

		context.Response.Body.Position = 0;
		using var reader = new StreamReader(context.Response.Body);
		var payload = await reader.ReadToEndAsync();
		var response = JsonSerializer.Deserialize<ApiErrorResponse>(payload, new JsonSerializerOptions
		{
			PropertyNameCaseInsensitive = true
		});

		Assert.NotNull(response);
		Assert.Equal("INVALID_OPERATION", response.ErrorCode);
		Assert.Equal("Boom", response.Message);
		Assert.Equal("trace-123", response.TraceId);
	}

	private sealed class ThrowingCriticalErrorLogService : ICriticalErrorLogService
	{
		public Task<CriticalErrorLogEntry> LogAsync(CriticalErrorLogEntry entry, CancellationToken cancellationToken = default)
			=> throw new InvalidOperationException("Database write failed");

		public Task<IReadOnlyList<CriticalErrorLogEntry>> GetRecentAsync(int limit = 25, CancellationToken cancellationToken = default)
			=> Task.FromResult<IReadOnlyList<CriticalErrorLogEntry>>([]);

		public Task ApplyRetentionPolicyAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
	}

	private sealed class StubWebHostEnvironment : IWebHostEnvironment
	{
		public string ApplicationName { get; set; } = "VibeSwarm.Tests";
		public IFileProvider WebRootFileProvider { get; set; } = null!;
		public string WebRootPath { get; set; } = string.Empty;
		public string EnvironmentName { get; set; } = "Production";
		public string ContentRootPath { get; set; } = string.Empty;
		public IFileProvider ContentRootFileProvider { get; set; } = null!;
	}
}
