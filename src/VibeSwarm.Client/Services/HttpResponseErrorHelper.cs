using System.Net;
using System.Text.Json;

namespace VibeSwarm.Client.Services;

internal static class HttpResponseErrorHelper
{
	public static async Task EnsureSuccessAsync(
		HttpResponseMessage response,
		CancellationToken cancellationToken,
		string? notFoundMessage = null)
	{
		if (response.IsSuccessStatusCode)
		{
			return;
		}

		var errorMessage = await TryReadErrorMessageAsync(response, cancellationToken);
		if (!string.IsNullOrWhiteSpace(errorMessage))
		{
			throw new HttpRequestException(errorMessage, null, response.StatusCode);
		}

		if (response.StatusCode == HttpStatusCode.NotFound && !string.IsNullOrWhiteSpace(notFoundMessage))
		{
			throw new HttpRequestException(notFoundMessage, null, response.StatusCode);
		}

		response.EnsureSuccessStatusCode();
	}

	private static async Task<string?> TryReadErrorMessageAsync(HttpResponseMessage response, CancellationToken cancellationToken)
	{
		var body = await response.Content.ReadAsStringAsync(cancellationToken);
		if (string.IsNullOrWhiteSpace(body))
		{
			return null;
		}

		try
		{
			using var document = JsonDocument.Parse(body);
			var root = document.RootElement;
			var messages = new List<string>();

			if (root.TryGetProperty("message", out var messageProperty) && messageProperty.ValueKind == JsonValueKind.String)
			{
				messages.Add(messageProperty.GetString()!);
			}

			if (root.TryGetProperty("error", out var errorProperty) && errorProperty.ValueKind == JsonValueKind.String)
			{
				messages.Add(errorProperty.GetString()!);
			}

			if (root.TryGetProperty("errors", out var errorsProperty) && errorsProperty.ValueKind == JsonValueKind.Object)
			{
				foreach (var property in errorsProperty.EnumerateObject())
				{
					if (property.Value.ValueKind != JsonValueKind.Array)
					{
						continue;
					}

					var fieldName = FormatFieldName(property.Name);
					foreach (var item in property.Value.EnumerateArray())
					{
						if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
						{
							messages.Add(string.IsNullOrWhiteSpace(fieldName)
								? item.GetString()!
								: $"{fieldName}: {item.GetString()}");
						}
					}
				}
			}

			if (messages.Count == 0 &&
				root.TryGetProperty("title", out var titleProperty) &&
				titleProperty.ValueKind == JsonValueKind.String)
			{
				messages.Add(titleProperty.GetString()!);
			}

			return messages.Count > 0
				? string.Join(" ", messages.Distinct(StringComparer.Ordinal))
				: body.Trim();
		}
		catch (JsonException)
		{
			return body.Trim();
		}
	}

	private static string FormatFieldName(string fieldName)
	{
		if (string.IsNullOrWhiteSpace(fieldName))
		{
			return string.Empty;
		}

		var name = fieldName.Split('.').Last();
		return string.Concat(name.Select((character, index) =>
			index > 0 && char.IsUpper(character) && !char.IsUpper(name[index - 1])
				? $" {character}"
				: character.ToString()));
	}
}
