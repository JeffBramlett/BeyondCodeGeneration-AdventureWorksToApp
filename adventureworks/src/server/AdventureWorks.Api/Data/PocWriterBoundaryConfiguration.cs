using System.Security.Cryptography;
using System.Text;

namespace AdventureWorks.Api.Data;

public sealed class PocWriterBoundaryConfiguration
{
	public const string CredentialFileEnvironmentVariable = "ADVENTUREWORKS_POC_API_WRITER_CREDENTIAL_FILE";
	public const string TargetServer = "localhost";
	public const string TargetDatabase = "AdventureWorks2022_Test";
	public const string RuntimeLogin = "AdventureWorksPocApiWriter";
	private const string CredentialRelativePath = "AdventureWorks\\poc-api-writer.credential";

	private PocWriterBoundaryConfiguration(bool hasUsableCredential)
	{
		HasUsableCredential = hasUsableCredential;
	}

	public bool HasUsableCredential { get; }

	public bool IsConfigured => HasUsableCredential;

	public static PocWriterBoundaryConfiguration FromEnvironment(
		Func<string, string?>? getEnvironmentVariable = null)
	{
		getEnvironmentVariable ??= Environment.GetEnvironmentVariable;
		var credentialPath = getEnvironmentVariable(CredentialFileEnvironmentVariable);
		if (string.IsNullOrWhiteSpace(credentialPath))
		{
			var localAppData = getEnvironmentVariable("LOCALAPPDATA");
			credentialPath = string.IsNullOrWhiteSpace(localAppData)
				? null
				: Path.Combine(localAppData, CredentialRelativePath);
		}

		return new PocWriterBoundaryConfiguration(HasValidProtectedCredential(credentialPath));
	}

	private static bool HasValidProtectedCredential(string? credentialPath)
	{
		if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(credentialPath) || !File.Exists(credentialPath))
		{
			return false;
		}

		byte[]? protectedBytes = null;
		byte[]? clearBytes = null;
		char[]? passwordCharacters = null;
		try
		{
			protectedBytes = Convert.FromHexString(File.ReadAllText(credentialPath).Trim());
			clearBytes = ProtectedData.Unprotect(protectedBytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
			passwordCharacters = Encoding.Unicode.GetChars(clearBytes);
			return passwordCharacters.Any(character => !char.IsWhiteSpace(character));
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or FormatException or CryptographicException or ArgumentException)
		{
			return false;
		}
		finally
		{
			if (protectedBytes is not null)
			{
				CryptographicOperations.ZeroMemory(protectedBytes);
			}

			if (clearBytes is not null)
			{
				CryptographicOperations.ZeroMemory(clearBytes);
			}

			if (passwordCharacters is not null)
			{
				Array.Clear(passwordCharacters);
			}
		}
	}
}