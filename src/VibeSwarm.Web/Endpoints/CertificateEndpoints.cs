using System.Security.Cryptography.X509Certificates;

namespace VibeSwarm.Web.Endpoints;

public static class CertificateEndpoints
{
	public static IEndpointRouteBuilder MapCertificateEndpoints(this IEndpointRouteBuilder endpoints)
	{
		endpoints.MapGet("/cert", HandleCertificateDownload).AllowAnonymous();
		return endpoints;
	}

	private static IResult HandleCertificateDownload(ILogger<Program> logger)
	{
		var certPath = Path.Combine(AppContext.BaseDirectory, "vibeswarm.pfx");

		if (!File.Exists(certPath))
		{
			logger.LogWarning("Certificate file not found at {CertPath}", certPath);
			return Results.NotFound("Certificate not found.");
		}

		var pfx = X509CertificateLoader.LoadPkcs12FromFile(certPath, "vibeswarm-dev-cert");

		// Export as DER-encoded certificate (public key only, no private key).
		// This is the format iOS expects for profile installation.
		var derBytes = pfx.Export(X509ContentType.Cert);

		logger.LogInformation("Certificate downloaded for device trust installation");

		return Results.File(
			derBytes,
			contentType: "application/x-x509-ca-cert",
			fileDownloadName: "vibeswarm.crt");
	}
}
