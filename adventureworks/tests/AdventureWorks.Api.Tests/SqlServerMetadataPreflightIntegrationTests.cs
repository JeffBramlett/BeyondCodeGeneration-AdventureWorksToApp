using AdventureWorks.Api.Data;
using Microsoft.Data.SqlClient;
using Xunit;
using Xunit.Abstractions;

namespace AdventureWorks.Api.Tests;

public sealed class SqlServerMetadataPreflightIntegrationTests(ITestOutputHelper output)
{
	[SqlServerIntegrationFact]
	public async Task ReadOnlyPreflight_ReportsCloneAndLedgerNameMetadata()
	{
		var configuration = SqlServerReadAdapterConfiguration.FromEnvironment();
		Assert.True(configuration.IsConfigured);
		var settings = new SqlConnectionStringBuilder(configuration.ConnectionString);
		Assert.Equal("localhost", settings.DataSource);
		Assert.Equal("AdventureWorks2022_Test", settings.InitialCatalog);
		Assert.Equal("AdventureWorksTestReader", settings.UserID);
		Assert.False(settings.IntegratedSecurity);

		await using var connection = new SqlConnection(configuration.ConnectionString);
		await connection.OpenAsync();

		await using (var identityCommand = connection.CreateCommand())
		{
			identityCommand.CommandText = """
				SELECT CONVERT(nvarchar(128), SERVERPROPERTY('ServerName')) AS ServerName,
				       DB_NAME() AS DatabaseName,
				       ORIGINAL_LOGIN() AS OriginalLogin,
				       USER_NAME() AS DatabaseUser,
				       IS_SRVROLEMEMBER(N'sysadmin') AS IsSysadmin,
				       IS_MEMBER(N'db_owner') AS IsDbOwner,
				       HAS_PERMS_BY_NAME(NULL, NULL, N'VIEW ANY DATABASE') AS CanViewAnyDatabase;
				""";
			await using var reader = await identityCommand.ExecuteReaderAsync();
			Assert.True(await reader.ReadAsync());
			var server = reader.GetString(0);
			var database = reader.GetString(1);
			var login = reader.GetString(2);
			var databaseUser = reader.GetString(3);
			var isSysadmin = reader.GetInt32(4);
			var isDbOwner = reader.GetInt32(5);
			var canViewAnyDatabase = reader.GetInt32(6);
			Assert.Equal("jeffs_dev_pc", server);
			Assert.Equal("AdventureWorks2022_Test", database);
			Assert.Equal("AdventureWorksTestReader", login);
			Assert.Equal("AdventureWorksTestReader", databaseUser);
			Assert.Equal(0, isSysadmin);
			Assert.Equal(0, isDbOwner);
			Assert.Equal(1, canViewAnyDatabase);
			output.WriteLine($"Verified read-only target: server={server}; database={database}; login={login}; user={databaseUser}; sysadmin={isSysadmin}; db_owner={isDbOwner}; view_any_database={canViewAnyDatabase}.");
		}

		await ReportAsync(
			connection,
			"candidate ledger database name",
			"SELECT name, state_desc FROM sys.databases WHERE name = @databaseName;",
			command => command.Parameters.Add("@databaseName", System.Data.SqlDbType.NVarChar, 128).Value = "AdventureWorksPocIdempotency");

		await ReportAsync(
			connection,
			"clone database settings",
			"""
			SELECT name, state_desc, user_access_desc,
			       SUSER_SNAME(owner_sid) AS owner_name,
			       recovery_model_desc, compatibility_level, collation_name,
			       is_read_only, is_auto_close_on, is_auto_shrink_on
			FROM sys.databases
			WHERE database_id = DB_ID();
			""");

		await ReportAsync(
			connection,
			"candidate schema name in clone",
			"SELECT name FROM sys.schemas WHERE name = @schemaName;",
			command => command.Parameters.Add("@schemaName", System.Data.SqlDbType.NVarChar, 128).Value = "Idempotency");

		await ReportAsync(
			connection,
			"clone database files",
			"""
			SELECT name, type_desc, physical_name,
			       CONVERT(decimal(18,2), size * 8.0 / 1024) AS allocated_mb,
			       max_size, growth, is_percent_growth
			FROM sys.database_files
			ORDER BY file_id;
			""");

		await ReportAsync(
			connection,
			"SQL-visible clone volume capacity",
			"""
			SELECT files.name, volume.volume_mount_point,
			       volume.total_bytes, volume.available_bytes
			FROM sys.database_files AS files
			CROSS APPLY sys.dm_os_volume_stats(DB_ID(), files.file_id) AS volume
			ORDER BY files.file_id;
			""");

		await ReportAsync(
			connection,
			"recorded clone backup destinations",
			"""
			SELECT TOP (10) backupset.type,
			       backupset.backup_start_date,
			       backupset.backup_finish_date,
			       backupset.backup_size,
			       backupmediafamily.physical_device_name
			FROM msdb.dbo.backupset AS backupset
			INNER JOIN msdb.dbo.backupmediafamily AS backupmediafamily
			  ON backupset.media_set_id = backupmediafamily.media_set_id
			WHERE backupset.database_name = DB_NAME()
			ORDER BY backupset.backup_finish_date DESC;
			""");
	}

	private async Task ReportAsync(
		SqlConnection connection,
		string label,
		string query,
		Action<SqlCommand>? configure = null)
	{
		await using var command = connection.CreateCommand();
		command.CommandText = query;
		command.CommandTimeout = 10;
		configure?.Invoke(command);
		try
		{
			await using var reader = await command.ExecuteReaderAsync();
			var rows = new List<string>();
			while (await reader.ReadAsync())
			{
				var values = new string[reader.FieldCount];
				for (var index = 0; index < reader.FieldCount; index++)
				{
					values[index] = reader.IsDBNull(index) ? "NULL" : Convert.ToString(reader.GetValue(index)) ?? "NULL";
				}

				rows.Add(string.Join(" | ", values));
			}

			output.WriteLine($"{label}: {(rows.Count == 0 ? "no visible rows" : string.Join("; ", rows))}");
		}
		catch (SqlException exception)
		{
			output.WriteLine($"{label}: unavailable ({exception.Number})");
		}
	}
}