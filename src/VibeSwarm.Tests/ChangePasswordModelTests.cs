using System.ComponentModel.DataAnnotations;
using VibeSwarm.Client.Models;

namespace VibeSwarm.Tests;

public sealed class ChangePasswordModelTests
{
	[Fact]
	public void Validate_WithMismatchedConfirmation_AssignsErrorToConfirmPassword()
	{
		var model = new ChangePasswordModel
		{
			CurrentPassword = "current-password",
			NewPassword = "NewPassword1",
			ConfirmPassword = "DifferentPassword1"
		};

		var results = new List<ValidationResult>();
		var isValid = Validator.TryValidateObject(model, new ValidationContext(model), results, validateAllProperties: true);

		Assert.False(isValid);
		var mismatch = Assert.Single(results);
		Assert.Equal("Passwords do not match.", mismatch.ErrorMessage);
		Assert.Contains(nameof(ChangePasswordModel.ConfirmPassword), mismatch.MemberNames);
	}
}
