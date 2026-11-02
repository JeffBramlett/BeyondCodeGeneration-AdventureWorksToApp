using AdventureWorks.Api.Data;
using Xunit;

namespace AdventureWorks.Api.Tests;

public sealed class OrderPermissionGuardTests
{
	private static readonly string[] BasePermissions =
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

	private static string[] ApprovedPermissions => BasePermissions.Concat(OrderReadSql.RequiredPermissions).ToArray();

	[Fact]
	public void OrderColumnSnapshot_InspectsEveryColumnOnOnlyTheTwoOrderTables()
	{
		var query = SqlServerPrincipalGuard.OrderColumnPermissionSnapshotSql;

		// Object-level securables with subentity rows; three-part column securables return no rows in SQL Server.
		Assert.Contains("sys.fn_my_permissions(N'[Sales].[SalesOrderHeader]', 'OBJECT')", query);
		Assert.Contains("sys.fn_my_permissions(N'[Sales].[SalesOrderDetail]', 'OBJECT')", query);
		Assert.Equal(2, CountOccurrences(query, "permission.subentity_name <> N''"));
		Assert.Contains("N':Sales.SalesOrderHeader.'", query);
		Assert.Contains("N':Sales.SalesOrderDetail.'", query);
		Assert.DoesNotContain("sys.columns", query);
		Assert.DoesNotContain("QUOTENAME(columnInfo.name)", query);
		Assert.DoesNotContain("SalesOrderHeader.*", query);
	}

	[Fact]
	public void ObjectSnapshot_ExcludesColumnLevelSelectRowsButKeepsColumnLevelMutations()
	{
		var query = SqlServerPrincipalGuard.ObjectPermissionSnapshotSql;

		Assert.Contains("sys.fn_my_permissions", query);
		Assert.Contains("'OBJECT'", query);
		Assert.Contains("permission.subentity_name = N'' OR permission.permission_name <> N'SELECT'", query);
	}

	[Fact]
	public void Guard_AcceptsCorrectedSnapshotShapeForExactColumnGrants()
	{
		// Shape verified by the Database Engineer against the live clone after the exact 7-column grant:
		// object snapshot has no Sales order table entry; column snapshot has exactly the 7 column entries.
		var objectLevel = BasePermissions;
		var columnLevel = new[]
		{
			"SELECT:Sales.SalesOrderHeader.SalesOrderID",
			"SELECT:Sales.SalesOrderHeader.SalesOrderNumber",
			"SELECT:Sales.SalesOrderHeader.OrderDate",
			"SELECT:Sales.SalesOrderDetail.SalesOrderID",
			"SELECT:Sales.SalesOrderDetail.SalesOrderDetailID",
			"SELECT:Sales.SalesOrderDetail.ProductID",
			"SELECT:Sales.SalesOrderDetail.OrderQty"
		};

		SqlServerPrincipalGuard.EnsureOrderReadPermissions(Snapshot(objectLevel.Concat(columnLevel)));
	}

	[Fact]
	public void Guard_RejectsLegacySnapshotShapeWhereColumnGrantsSurfacedAsTableSelect()
	{
		// Pre-fix behavior: column grants surfaced as table-wide SELECT and column rows were missing.
		var legacy = BasePermissions.Concat(["SELECT:Sales.SalesOrderHeader", "SELECT:Sales.SalesOrderDetail"]);

		Assert.Throws<SqlServerPrincipalGuardException>(() =>
			SqlServerPrincipalGuard.EnsureOrderReadPermissions(Snapshot(legacy)));
	}

	private static int CountOccurrences(string text, string value)
	{
		var count = 0;
		for (var index = text.IndexOf(value, StringComparison.Ordinal); index >= 0; index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal))
		{
			count++;
		}

		return count;
	}

	[Fact]
	public void Guard_AllowsExactlyTheRequiredOrderColumnReadsIncludingEffectiveInheritedReads()
	{
		// The snapshot represents effective permissions; SQL Server's grant source is immaterial.
		SqlServerPrincipalGuard.EnsureOrderReadPermissions(Snapshot(
			ApprovedPermissions.Append("SELECT:Sales.CustomerAddress")));
	}

	[Theory]
	[InlineData("SELECT:Sales.SalesOrderHeader.SalesOrderID")]
	[InlineData("SELECT:Sales.SalesOrderHeader.SalesOrderNumber")]
	[InlineData("SELECT:Sales.SalesOrderHeader.OrderDate")]
	[InlineData("SELECT:Sales.SalesOrderDetail.SalesOrderID")]
	[InlineData("SELECT:Sales.SalesOrderDetail.SalesOrderDetailID")]
	[InlineData("SELECT:Sales.SalesOrderDetail.ProductID")]
	[InlineData("SELECT:Sales.SalesOrderDetail.OrderQty")]
	public void Guard_RejectsEachMissingOrderColumn(string missingPermission)
	{
		var permissions = ApprovedPermissions.Where(permission => permission != missingPermission);

		Assert.Throws<SqlServerPrincipalGuardException>(() =>
			SqlServerPrincipalGuard.EnsureOrderReadPermissions(Snapshot(permissions)));
	}

	[Theory]
	[InlineData("SELECT:Sales.SalesOrderHeader.DueDate")]
	[InlineData("SELECT:Sales.SalesOrderDetail.UnitPrice")]
	[InlineData("SELECT:Sales.SalesOrderHeader")]
	[InlineData("SELECT:Sales.SalesOrderDetail")]
	[InlineData("SELECT:SCHEMA:Sales")]
	[InlineData("SELECT")]
	public void Guard_RejectsExtraColumnOrBroaderOrderReads(string extraPermission)
	{
		Assert.Throws<SqlServerPrincipalGuardException>(() =>
			SqlServerPrincipalGuard.EnsureOrderReadPermissions(Snapshot(ApprovedPermissions.Append(extraPermission))));
	}

	[Fact]
	public void Guard_RejectsBroadAndMissingPermissionsTogether()
	{
		var permissions = ApprovedPermissions
			.Where(permission => permission != "SELECT:Sales.SalesOrderDetail.OrderQty")
			.Append("SELECT:Sales.SalesOrderHeader");

		var failure = Assert.Throws<SqlServerPrincipalGuardException>(() =>
			SqlServerPrincipalGuard.EnsureOrderReadPermissions(Snapshot(permissions)));

		Assert.NotEmpty(failure.UnexpectedPermissions);
		Assert.NotEmpty(failure.MissingPermissions);
	}

	private static SqlServerPrincipalSnapshot Snapshot(IEnumerable<string> permissions) =>
		new(false, false, permissions.ToArray());
}
