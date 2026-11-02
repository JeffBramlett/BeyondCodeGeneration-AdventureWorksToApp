using AdventureWorks.Api.Application;
using AdventureWorks.Api.Contracts;
using Xunit;

namespace AdventureWorks.Api.Tests;

public sealed class CanonicalOrderRequestFingerprintTests
{
	[Fact]
	public void Canonicalize_UsesFixedPropertyOrderAndExplicitNullOffer()
	{
		var draft = Draft([new ValidatedOrderDraftLineShape(7, 3, null)]);

		var canonical = CanonicalOrderRequestFingerprint.Canonicalize(draft);

		Assert.Equal(
			"{\"canonicalizationVersion\":1,\"customerId\":42,\"billToAddressId\":11,\"shipToAddressId\":12,\"shipMethodId\":2,\"lines\":[{\"productId\":7,\"quantity\":3,\"offerId\":null}]}",
			canonical);
	}

	[Fact]
	public void Compute_MatchesAcceptedSha256DomainVector()
	{
		var draft = Draft([new ValidatedOrderDraftLineShape(7, 3, null)]);

		Assert.Equal(
			"52f33fb388cc27e070d31d9e1631171ffec1d82052e12bb8daaba22b60ce2824",
			CanonicalOrderRequestFingerprint.Compute(draft));
	}

	[Fact]
	public void Compute_IsDeterministicForEquivalentValidatedRequests()
	{
		var first = Draft([new ValidatedOrderDraftLineShape(7, 3, null)]);
		var second = Draft([new ValidatedOrderDraftLineShape(7, 3, null)]);

		Assert.Equal(
			CanonicalOrderRequestFingerprint.Compute(first),
			CanonicalOrderRequestFingerprint.Compute(second));
	}

	[Fact]
	public void Compute_ChangesWhenLogicalRequestFieldsChange()
	{
		var original = Draft([new ValidatedOrderDraftLineShape(7, 3, null)]);
		var changedQuantity = Draft([new ValidatedOrderDraftLineShape(7, 4, null)]);
		var changedOffer = Draft([new ValidatedOrderDraftLineShape(7, 3, 5)]);

		Assert.NotEqual(
			CanonicalOrderRequestFingerprint.Compute(original),
			CanonicalOrderRequestFingerprint.Compute(changedQuantity));
		Assert.NotEqual(
			CanonicalOrderRequestFingerprint.Compute(original),
			CanonicalOrderRequestFingerprint.Compute(changedOffer));
	}

	[Fact]
	public void Compute_ChangesWhenHeaderReferencesChange()
	{
		var original = Draft([new ValidatedOrderDraftLineShape(7, 3, null)]);
		var changedCustomer = original with { CustomerId = 43 };
		var changedBillTo = original with { BillToAddressId = 13 };
		var changedShipTo = original with { ShipToAddressId = 14 };
		var changedShipMethod = original with { ShipMethodId = 3 };
		var originalHash = CanonicalOrderRequestFingerprint.Compute(original);

		Assert.NotEqual(originalHash, CanonicalOrderRequestFingerprint.Compute(changedCustomer));
		Assert.NotEqual(originalHash, CanonicalOrderRequestFingerprint.Compute(changedBillTo));
		Assert.NotEqual(originalHash, CanonicalOrderRequestFingerprint.Compute(changedShipTo));
		Assert.NotEqual(originalHash, CanonicalOrderRequestFingerprint.Compute(changedShipMethod));
	}

	[Fact]
	public void Compute_ReturnsLowercaseSha256Hex()
	{
		var hash = CanonicalOrderRequestFingerprint.Compute(
			Draft([new ValidatedOrderDraftLineShape(7, 3, null)]));

		Assert.Equal(64, hash.Length);
		Assert.Equal(hash.ToLowerInvariant(), hash);
		Assert.All(hash, character => Assert.Contains(character, "0123456789abcdef"));
	}

	[Fact]
	public void Compute_PreservesLineOrder()
	{
		var first = Draft([
			new ValidatedOrderDraftLineShape(7, 1, null),
			new ValidatedOrderDraftLineShape(8, 2, null)
		]);
		var reversed = Draft([
			new ValidatedOrderDraftLineShape(8, 2, null),
			new ValidatedOrderDraftLineShape(7, 1, null)
		]);

		Assert.NotEqual(
			CanonicalOrderRequestFingerprint.Compute(first),
			CanonicalOrderRequestFingerprint.Compute(reversed));
	}

	[Fact]
	public void CanonicalizationVersion_IsIncludedInCanonicalJson()
	{
		Assert.Contains(
			"\"canonicalizationVersion\":1",
			CanonicalOrderRequestFingerprint.Canonicalize(Draft([new ValidatedOrderDraftLineShape(7, 1, null)])));
	}

	private static ValidatedOrderDraftShape Draft(IReadOnlyList<ValidatedOrderDraftLineShape> lines) => new(
		CustomerId: 42,
		BillToAddressId: 11,
		ShipToAddressId: 12,
		ShipMethodId: 2,
		Lines: lines);
}