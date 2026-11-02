using AdventureWorks.Api.Contracts;
using AdventureWorks.Api.Data;
using Xunit;

namespace AdventureWorks.Api.Tests;

public sealed class CustomerOrderOptionsSqlTests
{
	[Fact]
	public void AddressPlan_UsesBothCustomerEntityReferencesAndVerifiedAddressLinks()
	{
		const int customerId = 42;
		var plan = CustomerOrderOptionsSql.BuildAddresses(customerId);

		Assert.Equal(customerId, plan.Parameters["@customerId"]);
		Assert.Contains("FROM (VALUES (c.PersonID), (c.StoreID))", plan.CommandText);
		Assert.Contains("Person.BusinessEntityAddress AS businessEntityAddress", plan.CommandText);
		Assert.Contains("Person.Address AS address", plan.CommandText);
		Assert.Contains("Person.AddressType AS addressType", plan.CommandText);
		Assert.Contains("SELECT DISTINCT address.AddressID", plan.CommandText);
		Assert.Contains("OUTER APPLY", plan.CommandText);
		Assert.DoesNotContain("BillToAddressID", plan.CommandText);
		Assert.DoesNotContain("ShipToAddressID", plan.CommandText);
		Assert.DoesNotContain("FirstName", plan.CommandText);
		Assert.DoesNotContain("EmailAddress", plan.CommandText);
	}

	[Fact]
	public void ShipMethodPlan_IsBoundedAndOmitsFreightValues()
	{
		var plan = CustomerOrderOptionsSql.BuildShipMethods();

		Assert.Equal(CustomerOrderOptionsSql.MaximumShipMethods, plan.Parameters["@take"]);
		Assert.Contains("SELECT TOP (@take) ShipMethodID, Name", plan.CommandText);
		Assert.Contains("ORDER BY ShipMethodID", plan.CommandText);
		Assert.DoesNotContain("ShipBase", plan.CommandText);
		Assert.DoesNotContain("ShipRate", plan.CommandText);
	}

	[Fact]
	public void Plans_RejectInvalidCustomerIdsAndExposeMinimalDtos()
	{
		Assert.Throws<ArgumentOutOfRangeException>(() => CustomerOrderOptionsSql.BuildAddresses(0));
		Assert.Equal(
			new[] { "Addresses", "ShipMethods" },
			typeof(CustomerOrderOptionsDto).GetProperties().Select(property => property.Name));
		Assert.Equal(
			new[] { "AddressId", "AddressLine1", "AddressLine2", "City", "PostalCode", "AddressTypes" },
			typeof(CustomerAddressOptionDto).GetProperties().Select(property => property.Name));
		Assert.Equal(
			new[] { "ShipMethodId", "Name" },
			typeof(ShipMethodOptionDto).GetProperties().Select(property => property.Name));
	}

	[Fact]
	public void PrincipalGuard_RequiresOrderOptionPermissionsOnlyWhenExplicitlyRequested()
	{
		var baseline = new[]
		{
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
		};
		var snapshot = new SqlServerPrincipalSnapshot(false, false, baseline);

		SqlServerPrincipalGuard.EnsureLeastPrivilege(snapshot);
		Assert.Throws<SqlServerPrincipalGuardException>(() => SqlServerPrincipalGuard.EnsureLeastPrivilege(
			snapshot,
			CustomerOrderOptionsSql.RequiredPermissions));

		SqlServerPrincipalGuard.EnsureLeastPrivilege(new SqlServerPrincipalSnapshot(
			false,
			false,
			baseline.Concat(CustomerOrderOptionsSql.RequiredPermissions).ToArray()),
			CustomerOrderOptionsSql.RequiredPermissions);
	}
}