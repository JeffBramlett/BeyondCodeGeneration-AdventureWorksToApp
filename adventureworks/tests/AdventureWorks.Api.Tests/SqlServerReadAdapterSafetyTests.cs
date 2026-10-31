using AdventureWorks.Api.Data;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;
using Xunit;

namespace AdventureWorks.Api.Tests;

public sealed class SqlServerReadAdapterSafetyTests
{
	private static readonly string[] SearchPermissions =
	[
		"CONNECT SQL",
		"VIEW ANY DATABASE",
		"VIEW ANY COLUMN ENCRYPTION KEY DEFINITION",
		"VIEW ANY COLUMN MASTER KEY DEFINITION",
		"CONNECT",
		"SELECT:Production.Product",
		"SELECT:Production.ProductSubcategory",
		"SELECT:Production.ProductCategory",
		"SELECT:Sales.SpecialOfferProduct",
		"SELECT:Sales.SpecialOffer",
		"SELECT:Sales.Customer",
		"SELECT:Person.Person",
		"SELECT:Sales.Store"
	];

	[Fact]
	public void Configuration_DecryptsCurrentUserProtectedCredentialFile()
	{
		if (!OperatingSystem.IsWindows())
		{
			return;
		}

		const string fakeConnectionString = "Server=localhost;Database=AdventureWorks2022_Test;User ID=AdventureWorksTestReader;Password=not-a-real-secret;";
		var credentialPath = Path.Combine(Path.GetTempPath(), $"aw-credential-{Guid.NewGuid():N}.txt");
		var requestedNames = new List<string>();
		try
		{
			var clearBytes = Encoding.UTF8.GetBytes(fakeConnectionString);
			var protectedBytes = ProtectedData.Protect(clearBytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
			try
			{
				File.WriteAllText(credentialPath, Convert.ToHexString(protectedBytes));
			}
			finally
			{
				CryptographicOperations.ZeroMemory(clearBytes);
				CryptographicOperations.ZeroMemory(protectedBytes);
			}

			var configuration = SqlServerReadAdapterConfiguration.FromEnvironment(name =>
			{
				requestedNames.Add(name);
				return name == SqlServerReadAdapterConfiguration.CredentialFileEnvironmentVariable
					? credentialPath
					: null;
			});

			Assert.True(configuration.IsConfigured);
			var connectionSettings = new SqlConnectionStringBuilder(configuration.ConnectionString);
			Assert.Equal("localhost", connectionSettings.DataSource);
			Assert.Equal("AdventureWorks2022_Test", connectionSettings.InitialCatalog);
			Assert.Equal("AdventureWorksTestReader", connectionSettings.UserID);
			Assert.False(connectionSettings.IntegratedSecurity);
			Assert.Contains(SqlServerReadAdapterConfiguration.CredentialFileEnvironmentVariable, requestedNames);
			Assert.DoesNotContain("ADVENTUREWORKS_READONLY_SQL_CONNECTION_STRING", requestedNames);
		}
		finally
		{
			if (File.Exists(credentialPath))
			{
				File.Delete(credentialPath);
			}
		}
	}

	[Fact]
	public void Configuration_IsUnconfiguredWhenCredentialFileIsMissingOrInvalid()
	{
		Assert.False(SqlServerReadAdapterConfiguration.FromEnvironment(_ => null).IsConfigured);
		Assert.False(SqlServerReadAdapterConfiguration.FromEnvironment(name =>
			name == SqlServerReadAdapterConfiguration.CredentialFileEnvironmentVariable
				? "  "
				: null).IsConfigured);

		var invalidCredentialPath = Path.Combine(Path.GetTempPath(), $"aw-invalid-credential-{Guid.NewGuid():N}.txt");
		try
		{
			File.WriteAllText(invalidCredentialPath, "not-hex-ciphertext");
			Assert.False(SqlServerReadAdapterConfiguration.FromEnvironment(name =>
				name == SqlServerReadAdapterConfiguration.CredentialFileEnvironmentVariable
					? invalidCredentialPath
					: null).IsConfigured);
		}
		finally
		{
			if (File.Exists(invalidCredentialPath))
			{
				File.Delete(invalidCredentialPath);
			}
		}
	}

	[Fact]
	public void PrincipalGuard_AllowsExactlyTheApprovedReadSelectPermissions()
	{
		SqlServerPrincipalGuard.EnsureLeastPrivilege(new SqlServerPrincipalSnapshot(
			IsSysAdmin: false,
			IsDbOwner: false,
			EffectivePermissions: SearchPermissions));
	}

	[Theory]
	[InlineData(true, false)]
	[InlineData(false, true)]
	public void PrincipalGuard_RejectsElevatedRoles(bool isSysAdmin, bool isDbOwner)
	{
		var exception = Assert.ThrowsAny<InvalidOperationException>(() =>
			SqlServerPrincipalGuard.EnsureLeastPrivilege(new SqlServerPrincipalSnapshot(
				isSysAdmin,
				isDbOwner,
				SearchPermissions)));

		Assert.Contains(isSysAdmin ? "sysadmin" : "db_owner", exception.Message);
	}

	[Fact]
	public void PrincipalGuard_RejectsMissingEachOfferPermission()
	{
		foreach (var permission in new[]
		{
			"SELECT:Sales.SpecialOfferProduct",
			"SELECT:Sales.SpecialOffer"
		})
		{
			var missing = SearchPermissions.Where(candidate => candidate != permission).ToArray();

			Assert.ThrowsAny<InvalidOperationException>(() => SqlServerPrincipalGuard.EnsureLeastPrivilege(
				new SqlServerPrincipalSnapshot(false, false, missing)));
		}
	}

	[Theory]
	[InlineData("INSERT:Sales.Customer")]
	[InlineData("UPDATE:Sales.Customer")]
	public void PrincipalGuard_RejectsUnrelatedWritePermissions(string permission)
	{
		var withWritePermission = SearchPermissions.Append(permission).ToArray();

		Assert.ThrowsAny<InvalidOperationException>(() => SqlServerPrincipalGuard.EnsureLeastPrivilege(
			new SqlServerPrincipalSnapshot(false, false, withWritePermission)));
	}

	[Fact]
	public void PrincipalGuard_RejectsMissingReadsAndAdditionalMutationPermissions()
	{
		var missing = SearchPermissions.Skip(1).ToArray();
		var additional = SearchPermissions.Append("INSERT:Sales.Customer").ToArray();

		Assert.ThrowsAny<InvalidOperationException>(() => SqlServerPrincipalGuard.EnsureLeastPrivilege(
			new SqlServerPrincipalSnapshot(false, false, missing)));
		Assert.ThrowsAny<InvalidOperationException>(() => SqlServerPrincipalGuard.EnsureLeastPrivilege(
			new SqlServerPrincipalSnapshot(false, false, additional)));
	}

	[Fact]
	public void PrincipalGuard_AllowsInheritedReadAndExecutePermissions()
	{
		var effective = SearchPermissions
			.Append("SELECT:Person.CK_Person_PersonType")
			.Append("EXECUTE:dbo.sp_helpdiagrams")
			.ToArray();

		SqlServerPrincipalGuard.EnsureLeastPrivilege(new SqlServerPrincipalSnapshot(false, false, effective));
	}
}