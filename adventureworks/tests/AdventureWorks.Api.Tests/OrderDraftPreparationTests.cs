using AdventureWorks.Api.Application;
using AdventureWorks.Api.Contracts;
using Xunit;

namespace AdventureWorks.Api.Tests;

public sealed class OrderDraftPreparationTests
{
	[Fact]
	public void InvalidRequests_AreRejected()
	{
		var invalidRequests = new OrderDraftRequest?[]
		{
			null,
			ValidRequest() with { CustomerId = 0 },
			ValidRequest() with { BillToAddressId = -1 },
			ValidRequest() with { ShipToAddressId = 0 },
			ValidRequest() with { ShipMethodId = -1 },
			ValidRequest() with { Lines = [] },
			ValidRequest() with { Lines = null },
			ValidRequest() with { Lines = [new OrderDraftLineRequest(0, 1, null)] },
			ValidRequest() with { Lines = [new OrderDraftLineRequest(1, 0, null)] },
			ValidRequest() with { Lines = [new OrderDraftLineRequest(1, 32768, null)] },
			ValidRequest() with { Lines = [new OrderDraftLineRequest(1, 1, 0)] },
			ValidRequest() with { Lines = [null] },
			ValidRequest() with
			{
				Lines = Enumerable.Repeat<OrderDraftLineRequest?>(new OrderDraftLineRequest(1, 1, null),
					OrderDraftRequestValidator.MaximumLineCount + 1).ToArray()
			}
		};

		foreach (var request in invalidRequests)
		{
			var preparation = new OrderDraftPreparation();
			var result = preparation.Prepare(request);

			Assert.False(result.IsValid);
			Assert.Null(result.Draft);
		}
	}

	[Theory]
	[InlineData(1)]
	[InlineData(32767)]
	public void ValidRequest_PreservesOnlyDraftFields(int quantity)
	{
		var preparation = new OrderDraftPreparation();
		var request = ValidRequest() with
		{
			Lines = [new OrderDraftLineRequest(7, quantity, 3)]
		};

		var result = preparation.Prepare(request);

		Assert.True(result.IsValid);
		Assert.Empty(result.Errors);
		Assert.Equal(7, Assert.Single(result.Draft!.Lines).ProductId);
		Assert.Equal(quantity, result.Draft.Lines[0].Quantity);
		Assert.Equal(3, result.Draft.Lines[0].OfferId);
	}

	[Fact]
	public void ValidatedDraft_CopiesRequestLinesAndExposesReadOnlyCollection()
	{
		var sourceLines = new List<OrderDraftLineRequest?>
		{
			new(7, 1, null),
			new(8, 2, 4)
		};
		var result = OrderDraftRequestValidator.Validate(ValidRequest() with { Lines = sourceLines });
		Assert.True(result.IsValid);

		sourceLines[0] = new OrderDraftLineRequest(99, 32768, null);
		sourceLines.Add(new OrderDraftLineRequest(10, 1, null));

		Assert.Equal(2, result.Draft!.Lines.Count);
		Assert.Equal(new ValidatedOrderDraftLineShape(7, 1, null), result.Draft.Lines[0]);
		Assert.Equal(new ValidatedOrderDraftLineShape(8, 2, 4), result.Draft.Lines[1]);

		var exposedLines = Assert.IsAssignableFrom<IList<ValidatedOrderDraftLineShape>>(result.Draft.Lines);
		Assert.Throws<NotSupportedException>(() => exposedLines[0] = new ValidatedOrderDraftLineShape(99, 32768, null));
	}

	[Fact]
	public void MaximumLineCount_IsAcceptedWhenProductIdsAreUnique()
	{
		var lines = Enumerable.Range(1, OrderDraftRequestValidator.MaximumLineCount)
			.Select(productId => (OrderDraftLineRequest?)new OrderDraftLineRequest(productId, 1, null))
			.ToArray();
		var request = ValidRequest() with { Lines = lines };

		var result = OrderDraftRequestValidator.Validate(request);

		Assert.True(result.IsValid);
		Assert.Empty(result.Errors);
		Assert.Equal(OrderDraftRequestValidator.MaximumLineCount, result.Draft!.Lines.Count);
	}

	[Fact]
	public void DuplicateProductIds_AreRejectedAtLaterLine()
	{
		var request = ValidRequest() with
		{
			Lines =
			[
				new OrderDraftLineRequest(7, 1, null),
				new OrderDraftLineRequest(8, 2, null),
				new OrderDraftLineRequest(7, 3, 4)
			]
		};

		var result = OrderDraftRequestValidator.Validate(request);

		Assert.False(result.IsValid);
		Assert.Null(result.Draft);
		Assert.Contains("lines[2].productId", result.Errors.Keys);
		Assert.DoesNotContain("lines[0].productId", result.Errors.Keys);
	}

	[Fact]
	public void OmittedOffer_IsAcceptedAsRequestShapeOnly()
	{
		var preparation = new OrderDraftPreparation();

		var result = preparation.Prepare(ValidRequest());

		Assert.True(result.IsValid);
		Assert.Null(Assert.Single(result.Draft!.Lines).OfferId);
	}

	[Fact]
	public void Preparation_HasNoWriterDependencyOrSubmissionMethod()
	{
		var constructor = Assert.Single(typeof(OrderDraftPreparation).GetConstructors());
		Assert.Empty(constructor.GetParameters());
		Assert.Equal(typeof(OrderDraftValidationResult),
			typeof(OrderDraftPreparation).GetMethod(nameof(OrderDraftPreparation.Prepare))!.ReturnType);
		Assert.Null(typeof(OrderDraftPreparation).Assembly.GetType(
			"AdventureWorks.Api.Application.IOrderDraftWriter"));
	}

	[Fact]
	public void RequestDto_ContainsNoClientOwnedFinancialOrGeneratedFields()
	{
		Assert.Equal(
			new[] { "BillToAddressId", "CustomerId", "Lines", "ShipMethodId", "ShipToAddressId" },
			typeof(OrderDraftRequest).GetProperties().Select(property => property.Name).Order().ToArray());
		Assert.Equal(
			new[] { "OfferId", "ProductId", "Quantity" },
			typeof(OrderDraftLineRequest).GetProperties().Select(property => property.Name).Order().ToArray());
	}

	private static OrderDraftRequest ValidRequest() => new(
		CustomerId: 42,
		BillToAddressId: 11,
		ShipToAddressId: 12,
		ShipMethodId: 2,
		Lines: [new OrderDraftLineRequest(7, 1, null)]);

}