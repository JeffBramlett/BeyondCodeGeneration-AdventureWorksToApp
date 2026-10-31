using AdventureWorks.Api.Application;
using AdventureWorks.Api.Contracts;
using AdventureWorks.Api.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AdventureWorks.Api.Tests;

public sealed class OrderSubmissionUnitTests
{
	[Fact]
	public void DemoFinancials_UseApprovedConstants()
	{
		Assert.Equal(25.00m, DemoOrderFinancials.FreightPerOrder);
		Assert.Equal(0.075m, DemoOrderFinancials.TaxRate);
		Assert.Equal(1, DemoOrderFinancials.NoOfferSpecialOfferId);
		// Existing POC calculator contract is unchanged.
		Assert.Equal(0.00m, PocOrderFinancialCalculator.ExplicitFreight);
	}

	[Theory]
	[InlineData(34.99, 0.02, 3, 102.8706)]
	[InlineData(8.99, 0, 2, 17.98)]
	[InlineData(2024.994, 0.15, 7, 12048.71430)]
	public void DemoFinancials_LineTotalMatchesSqlComputedFormula(decimal unitPrice, decimal discount, int quantity, decimal expected)
	{
		Assert.Equal(expected, DemoOrderFinancials.LineTotal(unitPrice, discount, quantity));
	}

	[Fact]
	public void DemoFinancials_SubtotalRoundsToMoneyScaleAndTaxRoundsAwayFromZero()
	{
		Assert.Equal(1.2346m, DemoOrderFinancials.Subtotal([1.23455m, 0m]));
		Assert.Equal(0.23m, DemoOrderFinancials.Tax(3.00m));
		Assert.Equal(9.06m, DemoOrderFinancials.Tax(120.8506m));
		Assert.Equal(154.9106m, DemoOrderFinancials.TotalDue(120.8506m, 9.06m, 25.00m));
	}

	[Fact]
	public void TokenRegistry_TracksInFlightCompletedAndReleasedStates()
	{
		var registry = new OrderSubmissionTokenRegistry(new FixedTimeProvider(DateTimeOffset.UnixEpoch));
		var token = Guid.NewGuid();
		var confirmation = Confirmation("SO1");

		Assert.Equal(SubmissionTokenState.Started, registry.TryBegin(token).State);
		Assert.Equal(SubmissionTokenState.InProgress, registry.TryBegin(token).State);
		registry.Release(token);
		Assert.Equal(SubmissionTokenState.Started, registry.TryBegin(token).State);
		registry.Complete(token, confirmation);

		var replay = registry.TryBegin(token);
		Assert.Equal(SubmissionTokenState.Completed, replay.State);
		Assert.Same(confirmation, replay.Confirmation);
	}

	[Fact]
	public void TokenRegistry_IsBoundedByCountAndAge()
	{
		var time = new MutableTimeProvider(DateTimeOffset.UnixEpoch);
		var registry = new OrderSubmissionTokenRegistry(time);
		var first = Guid.NewGuid();
		registry.TryBegin(first);
		registry.Complete(first, Confirmation("SO-first"));
		for (var index = 0; index < OrderSubmissionTokenRegistry.MaximumCompletedEntries; index++)
		{
			var token = Guid.NewGuid();
			registry.TryBegin(token);
			registry.Complete(token, Confirmation($"SO{index}"));
		}

		Assert.Equal(OrderSubmissionTokenRegistry.MaximumCompletedEntries, registry.CompletedCount);
		Assert.Equal(SubmissionTokenState.Started, registry.TryBegin(first).State);
		registry.Release(first);

		time.UtcNow = DateTimeOffset.UnixEpoch + OrderSubmissionTokenRegistry.CompletedRetention + TimeSpan.FromSeconds(1);
		registry.TryBegin(Guid.NewGuid());
		Assert.Equal(0, registry.CompletedCount);
	}

	[Fact]
	public void WriterConfiguration_MissingCredentialIsUnconfigured()
	{
		var configuration = SqlServerOrderWriterConfiguration.FromEnvironment(name => name switch
		{
			SqlServerOrderWriterConfiguration.CredentialFileEnvironmentVariable =>
				Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.credential"),
			_ => null
		});

		Assert.False(configuration.IsConfigured);
		Assert.Null(configuration.ConnectionString);
		Assert.Equal("ADVENTUREWORKS_TEST_WRITER_CREDENTIAL_FILE", SqlServerOrderWriterConfiguration.CredentialFileEnvironmentVariable);
	}

	[Fact]
	public void WriterConfiguration_InvalidCredentialFileIsUnconfigured()
	{
		var path = Path.Combine(Path.GetTempPath(), $"invalid-{Guid.NewGuid():N}.credential");
		File.WriteAllText(path, "not-hex");
		try
		{
			var configuration = SqlServerOrderWriterConfiguration.FromEnvironment(name =>
				name == SqlServerOrderWriterConfiguration.CredentialFileEnvironmentVariable ? path : null);
			Assert.False(configuration.IsConfigured);
		}
		finally
		{
			File.Delete(path);
		}
	}

	[Fact]
	public void WriteSql_IsParameterizedAndAvoidsOutputClauses()
	{
		var statements = new[]
		{
			OrderWriteSql.InsertHeader,
			OrderWriteSql.InsertDetail,
			OrderWriteSql.ReadBackHeader,
			OrderWriteSql.ReadBackDetails
		};
		foreach (var statement in statements)
		{
			Assert.DoesNotContain("OUTPUT", statement, StringComparison.OrdinalIgnoreCase);
		}

		Assert.Contains("SCOPE_IDENTITY()", OrderWriteSql.InsertHeader);
		foreach (var parameter in new[] { "@OrderDate", "@DueDate", "@CustomerID", "@BillToAddressID", "@ShipToAddressID", "@ShipMethodID", "@TaxAmt", "@Freight" })
		{
			Assert.Contains(parameter, OrderWriteSql.InsertHeader);
		}

		foreach (var parameter in new[] { "@SalesOrderID", "@OrderQty", "@ProductID", "@SpecialOfferID", "@UnitPrice", "@UnitPriceDiscount" })
		{
			Assert.Contains(parameter, OrderWriteSql.InsertDetail);
		}

		Assert.Contains("DB_NAME()", OrderWriteSql.IdentityCheck);
		Assert.Contains("SUSER_SNAME()", OrderWriteSql.IdentityCheck);
	}

	[Fact]
	public async Task WriteStore_RejectsWrongTargetWithoutConnecting()
	{
		// An unconfigured writer fails closed with the sanitized exception type.
		var store = new SqlServerOrderWriteStore(
			SqlServerOrderWriterConfiguration.FromEnvironment(_ => null),
			NullLogger<SqlServerOrderWriteStore>.Instance);

		Assert.False(store.IsConfigured);
		await Assert.ThrowsAsync<OrderWriteUnavailableException>(() =>
			store.CreateOrderAsync(new OrderWriteCommand(DateTime.Now, DateTime.Now, 1, 1, 1, 1, 0m, 25m, []), CancellationToken.None));
	}

	[Fact]
	public void OfferPricingSql_IsParameterizedAndScopedToProductOfferLink()
	{
		var plan = ProductCatalogSql.BuildOfferPricing(707, 2);

		Assert.Contains("offer.DiscountPct", plan.CommandText);
		Assert.Contains("offerProduct.ProductID = @productId", plan.CommandText);
		Assert.Contains("offerProduct.SpecialOfferID = @specialOfferId", plan.CommandText);
		Assert.Equal(707, plan.Parameters["@productId"]);
		Assert.Equal(2, plan.Parameters["@specialOfferId"]);
		Assert.Throws<ArgumentOutOfRangeException>(() => ProductCatalogSql.BuildOfferPricing(0, 2));
		Assert.Throws<ArgumentOutOfRangeException>(() => ProductCatalogSql.BuildOfferPricing(707, 0));
	}

	private static OrderSubmissionConfirmationDto Confirmation(string reference) => new(
		reference, "Recorded", DateTime.UnixEpoch, DateTime.UnixEpoch.AddDays(7), 1, 1, 1, 1, [], 0m, 0m, 25m, 25m);

	private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
	{
		public DateTimeOffset UtcNow { get; set; } = utcNow;

		public override DateTimeOffset GetUtcNow() => UtcNow;
	}
}

public sealed class SqlServerWriteIntegrationFactAttribute : FactAttribute
{
	public SqlServerWriteIntegrationFactAttribute()
	{
		if (Environment.GetEnvironmentVariable("ADVENTUREWORKS_RUN_SQL_WRITE_INTEGRATION_TESTS") != "1" ||
			!int.TryParse(Environment.GetEnvironmentVariable("ADVENTUREWORKS_WRITE_TEST_CUSTOMER_ID"), out _) ||
			!SqlServerReadAdapterConfiguration.FromEnvironment().IsConfigured ||
			!SqlServerOrderWriterConfiguration.FromEnvironment().IsConfigured)
		{
			Skip = "Demo order write integration is opt-in (ADVENTUREWORKS_RUN_SQL_WRITE_INTEGRATION_TESTS=1, ADVENTUREWORKS_WRITE_TEST_CUSTOMER_ID) and requires both clone credentials. It INSERTS one order into AdventureWorks2022_Test.";
		}
	}
}

public sealed class OrderSubmissionSqlIntegrationTests
{
	[SqlServerWriteIntegrationFact]
	public async Task DemoOrder_IsRecordedInCloneWithPersistedFinancials()
	{
		var customerId = int.Parse(Environment.GetEnvironmentVariable("ADVENTUREWORKS_WRITE_TEST_CUSTOMER_ID")!);
		var writerConfiguration = SqlServerOrderWriterConfiguration.FromEnvironment();
		Assert.Equal("AdventureWorks2022_Test", new SqlConnectionStringBuilder(writerConfiguration.ConnectionString).InitialCatalog);

		var reader = new SqlServerReadStore(SqlServerReadAdapterConfiguration.FromEnvironment(), NullLogger<SqlServerReadStore>.Instance);
		var writer = new SqlServerOrderWriteStore(writerConfiguration, NullLogger<SqlServerOrderWriteStore>.Instance);
		var options = await ((ICustomerReadStore)reader).GetOrderOptionsAsync(customerId, CancellationToken.None);
		Assert.NotNull(options);
		var address = Assert.IsType<CustomerAddressOptionDto>(options.Addresses.FirstOrDefault());
		var shipMethod = Assert.IsType<ShipMethodOptionDto>(options.ShipMethods.FirstOrDefault());

		var service = new OrderSubmissionService(
			reader,
			reader,
			reader,
			writer,
			new OrderSubmissionTokenRegistry(TimeProvider.System),
			TimeProvider.System,
			NullLogger<OrderSubmissionService>.Instance);
		var result = await service.SubmitAsync(new OrderSubmissionRequest(
			Guid.NewGuid().ToString(),
			customerId,
			address.AddressId,
			address.AddressId,
			shipMethod.ShipMethodId,
			[new OrderDraftLineRequest(680, 1, null)]), CancellationToken.None);

		Assert.Equal(OrderSubmissionOutcome.Created, result.Outcome);
		var confirmation = result.Confirmation!;
		Assert.StartsWith("SO", confirmation.OrderReference);
		var line = Assert.Single(confirmation.Lines);
		Assert.Equal(1, line.OfferId);
		Assert.Equal(line.LineTotal, confirmation.SubTotal);
		Assert.Equal(DemoOrderFinancials.Tax(confirmation.SubTotal), confirmation.TaxAmt);
		Assert.Equal(25.00m, confirmation.Freight);
		Assert.Equal(confirmation.SubTotal + confirmation.TaxAmt + confirmation.Freight, confirmation.TotalDue);
	}
}
