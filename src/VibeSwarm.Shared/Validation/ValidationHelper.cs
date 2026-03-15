using System.ComponentModel.DataAnnotations;

namespace VibeSwarm.Shared.Validation;

public static class ValidationHelper
{
	public static void ValidateObject(object instance)
	{
		ArgumentNullException.ThrowIfNull(instance);

		var validationResults = new List<ValidationResult>();
		var validationContext = new ValidationContext(instance);
		if (Validator.TryValidateObject(instance, validationContext, validationResults, validateAllProperties: true))
		{
			return;
		}

		throw new ValidationException(BuildMessage(validationResults));
	}

	public static string BuildMessage(IEnumerable<ValidationResult> validationResults)
	{
		var messages = validationResults
			.Select(result => result.ErrorMessage)
			.Where(message => !string.IsNullOrWhiteSpace(message))
			.Select(message => message!)
			.Distinct(StringComparer.Ordinal)
			.ToList();

		return messages.Count switch
		{
			0 => "Validation failed.",
			1 => messages[0],
			_ => string.Join(" ", messages)
		};
	}
}
