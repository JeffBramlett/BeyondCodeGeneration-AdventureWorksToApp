using AdventureWorks.Api.Application;
using AdventureWorks.Api.Contracts;
using Xunit;

namespace AdventureWorks.Api.Tests;

public sealed class LocalPriceReviewReceiptStoreTests
{
	private const string Issuer = "local-issuer";
	private const string Subject = "S-1-5-21-1001";

	[Fact]
	public void Issue_ReturnsOpaqueThirtyTwoByteBase64UrlHandleOnlyOnSuccess()
	{
		var store = new LocalPriceReviewReceiptStore();
		var first = Issue(store);
		var second = Issue(store);

		Assert.Equal(PriceReviewReceiptIssueStatus.Issued, first.Status);
		Assert.Equal(43, first.Handle!.Length);
		var decoded = Convert.FromBase64String(first.Handle.Replace('-', '+').Replace('_', '/') + "=");
		Assert.Equal(32, decoded.Length);
		Assert.NotEqual(first.Handle, second.Handle);
		Assert.Null(PriceReviewReceiptIssueResult.Failed(PriceReviewReceiptIssueStatus.InvalidInput).Handle);
		Assert.DoesNotContain(first.Handle, first.ToString(), StringComparison.Ordinal);
		Assert.DoesNotContain(Subject, first.ToString(), StringComparison.Ordinal);
		Assert.DoesNotContain("Digest", string.Join(',', typeof(LocalPriceReviewReceiptStore)
			.GetProperties(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public)
			.Select(property => property.Name)), StringComparison.OrdinalIgnoreCase);
		Assert.Same(LocalPriceReviewReceiptStore.ProcessInstance, LocalPriceReviewReceiptStore.ProcessInstance);
	}

	[Fact]
	public void Check_IsReusableUntilExpiryAndAcceptsKnownZero()
	{
		var clock = new ManualTimeProvider();
		var store = new LocalPriceReviewReceiptStore(clock);
		var handle = Issue(store, prices: Snapshot((7, 0m))).Handle!;

		Assert.Equal(PriceReviewReceiptCheckStatus.Current, Check(store, handle, fresh: Snapshot((7, 0m))).Status);
		Assert.Equal(PriceReviewReceiptCheckStatus.Current, Check(store, handle, fresh: Snapshot((7, 0m))).Status);
	}

	[Fact]
	public void Check_ExpiresAtExactFifteenMinuteBoundary()
	{
		var clock = new ManualTimeProvider();
		var store = new LocalPriceReviewReceiptStore(clock);
		var handle = Issue(store).Handle!;

		clock.Advance(LocalPriceReviewReceiptStore.ReceiptLifetime - TimeSpan.FromTicks(1));
		Assert.Equal(PriceReviewReceiptCheckStatus.Current, Check(store, handle).Status);
		clock.Advance(TimeSpan.FromTicks(1));
		Assert.Equal(PriceReviewReceiptCheckStatus.InvalidReceipt, Check(store, handle).Status);
	}

	[Fact]
	public void Check_RepeatedSuccessfulChecksDoNotExtendFixedLifetime()
	{
		var clock = new ManualTimeProvider();
		var store = new LocalPriceReviewReceiptStore(clock);
		var handle = Issue(store).Handle!;

		clock.Advance(LocalPriceReviewReceiptStore.ReceiptLifetime - TimeSpan.FromMinutes(1));
		Assert.Equal(PriceReviewReceiptCheckStatus.Current, Check(store, handle).Status);
		Assert.Equal(PriceReviewReceiptCheckStatus.Current, Check(store, handle).Status);

		clock.Advance(TimeSpan.FromMinutes(1));

		Assert.Equal(PriceReviewReceiptCheckStatus.InvalidReceipt, Check(store, handle).Status);
	}

	[Fact]
	public void Check_WrongIssuerOrSubjectDoesNotConsumeAnotherIdentityReceipt()
	{
		var store = new LocalPriceReviewReceiptStore();
		var handle = Issue(store).Handle!;

		Assert.Equal(PriceReviewReceiptCheckStatus.InvalidReceipt, Check(store, handle, issuer: "other-issuer").Status);
		Assert.Equal(PriceReviewReceiptCheckStatus.InvalidReceipt, Check(store, handle, subject: "S-1-5-21-2002").Status);
		Assert.Equal(PriceReviewReceiptCheckStatus.Current, Check(store, handle).Status);
	}

	[Fact]
	public void Check_MalformedOrUnknownHandleIsInvalid()
	{
		var store = new LocalPriceReviewReceiptStore();

		Assert.Equal(
			PriceReviewReceiptCheckStatus.InvalidReceipt,
			store.Check("not-a-handle", Issuer, Subject, Draft(), Snapshot((7, 12.5m))).Status);

		var otherStoreHandle = Issue(new LocalPriceReviewReceiptStore()).Handle!;
		Assert.Equal(
			PriceReviewReceiptCheckStatus.InvalidReceipt,
			Check(store, otherStoreHandle).Status);
	}

	[Fact]
	public void Check_ChangedDraftInvalidatesReceipt()
	{
		var store = new LocalPriceReviewReceiptStore();
		var handle = Issue(store).Handle!;
		var changedDraft = Draft(quantity: 2);

		Assert.Equal(
			PriceReviewReceiptCheckStatus.InvalidReceipt,
			store.Check(handle, Issuer, Subject, changedDraft, Snapshot((7, 12.5m))).Status);
		Assert.Equal(PriceReviewReceiptCheckStatus.InvalidReceipt, Check(store, handle).Status);
	}

	[Fact]
	public void Check_ChangedDraftFieldOrLineOrderInvalidatesReceipt()
	{
		var original = new ValidatedOrderDraftShape(
			CustomerId: 42,
			BillToAddressId: 11,
			ShipToAddressId: 12,
			ShipMethodId: 2,
			Lines:
			[
				new ValidatedOrderDraftLineShape(7, 1, null),
				new ValidatedOrderDraftLineShape(8, 3, 4)
			]);
		var reviewedPrices = Snapshot((7, 12.5m), (8, 0m));
		var changedDrafts = new[]
		{
			original with { CustomerId = 43 },
			original with { BillToAddressId = 13 },
			original with { ShipToAddressId = 14 },
			original with { ShipMethodId = 3 },
			new ValidatedOrderDraftShape(42, 11, 12, 2,
				[new ValidatedOrderDraftLineShape(9, 1, null), new ValidatedOrderDraftLineShape(8, 3, 4)]),
			new ValidatedOrderDraftShape(42, 11, 12, 2,
				[new ValidatedOrderDraftLineShape(7, 2, null), new ValidatedOrderDraftLineShape(8, 3, 4)]),
			new ValidatedOrderDraftShape(42, 11, 12, 2,
				[new ValidatedOrderDraftLineShape(7, 1, 5), new ValidatedOrderDraftLineShape(8, 3, 4)]),
			new ValidatedOrderDraftShape(42, 11, 12, 2,
				[new ValidatedOrderDraftLineShape(8, 3, 4), new ValidatedOrderDraftLineShape(7, 1, null)])
		};

		foreach (var changedDraft in changedDrafts)
		{
			var store = new LocalPriceReviewReceiptStore();
			var handle = store.Issue(Issuer, Subject, original, reviewedPrices).Handle!;

			Assert.Equal(
				PriceReviewReceiptCheckStatus.InvalidReceipt,
				store.Check(handle, Issuer, Subject, changedDraft, reviewedPrices).Status);
		}
	}

	[Fact]
	public void Issue_ReviewedSnapshotIsUnaffectedByCallerOwnedCollectionChanges()
	{
		var store = new LocalPriceReviewReceiptStore();
		var sourceEntries = new List<ProductListPriceSnapshotEntry?> { new(7, 12.5m) };
		var reviewedPrices = new ProductListPriceSnapshot(true, sourceEntries);
		var handle = store.Issue(Issuer, Subject, Draft(), reviewedPrices).Handle!;

		sourceEntries[0] = new ProductListPriceSnapshotEntry(7, 99m);

		Assert.Equal(
			PriceReviewReceiptCheckStatus.Current,
			Check(store, handle, fresh: Snapshot((7, 12.5m))).Status);
	}

	[Theory]
	[MemberData(nameof(ChangedOrMissingPrices))]
	public void Check_ChangedOrMissingPriceRequiresFreshReceipt(decimal? freshPrice)
	{
		var store = new LocalPriceReviewReceiptStore();
		var handle = Issue(store).Handle!;

		Assert.Equal(
			PriceReviewReceiptCheckStatus.ReviewRequired,
			Check(store, handle, fresh: Snapshot((7, freshPrice))).Status);
		Assert.Equal(PriceReviewReceiptCheckStatus.InvalidReceipt, Check(store, handle).Status);
	}

	public static TheoryData<decimal?> ChangedOrMissingPrices => new()
	{
		12.51m,
		null
	};

	[Fact]
	public void Check_TransientPriceSourceFailureIsDistinctAndDoesNotConsumeReceipt()
	{
		var store = new LocalPriceReviewReceiptStore();
		var handle = Issue(store).Handle!;
		var unavailable = new ProductListPriceSnapshot(isAvailable: false, entries: []);

		Assert.Equal(PriceReviewReceiptCheckStatus.PriceSourceUnavailable,
			Check(store, handle, fresh: unavailable).Status);
		Assert.Equal(PriceReviewReceiptCheckStatus.Current, Check(store, handle).Status);
	}

	[Fact]
	public void Check_ReceiptFromAnotherStoreInstanceIsInvalid()
	{
		var firstStore = new LocalPriceReviewReceiptStore();
		var secondStore = new LocalPriceReviewReceiptStore();
		var handle = Issue(firstStore).Handle!;

		Assert.Equal(PriceReviewReceiptCheckStatus.InvalidReceipt, Check(secondStore, handle).Status);
	}

	[Fact]
	public void Issue_RejectsIncompleteOrUnavailableReviewedPrices()
	{
		var store = new LocalPriceReviewReceiptStore();

		Assert.Equal(PriceReviewReceiptIssueStatus.InvalidInput, Issue(store, prices: Snapshot((8, 1m))).Status);
		Assert.Equal(PriceReviewReceiptIssueStatus.InvalidInput,
			Issue(store, prices: new ProductListPriceSnapshot(false, [])).Status);
		Assert.Equal(PriceReviewReceiptIssueStatus.InvalidInput, Issue(store, prices: Snapshot((7, null))).Status);
	}

	[Fact]
	public void Issue_EnforcesPerIdentityCapacityWithoutEvictingActiveReceipts()
	{
		var store = new LocalPriceReviewReceiptStore();
		var handles = Enumerable.Range(0, LocalPriceReviewReceiptStore.MaximumReceiptsPerIdentity)
			.Select(_ => Issue(store).Handle!)
			.ToArray();

		var rejected = Issue(store);

		Assert.Equal(PriceReviewReceiptIssueStatus.CapacityExceededRetryable, rejected.Status);
		Assert.Null(rejected.Handle);
		Assert.Equal(PriceReviewReceiptCheckStatus.Current, Check(store, handles[0]).Status);
		Assert.Equal(PriceReviewReceiptIssueStatus.Issued, Issue(store, subject: "S-1-5-21-2002").Status);
	}

	[Fact]
	public void Issue_EnforcesTotalCapacityWithoutEvictingActiveReceipts()
	{
		var store = new LocalPriceReviewReceiptStore();
		string? firstHandle = null;
		for (var index = 0; index < LocalPriceReviewReceiptStore.MaximumReceipts; index++)
		{
			var result = Issue(store, subject: $"subject-{index}");
			Assert.Equal(PriceReviewReceiptIssueStatus.Issued, result.Status);
			firstHandle ??= result.Handle;
		}

		Assert.Equal(
			PriceReviewReceiptIssueStatus.CapacityExceededRetryable,
			Issue(store, subject: "subject-over-cap").Status);
		Assert.Equal(PriceReviewReceiptCheckStatus.Current, Check(store, firstHandle!, subject: "subject-0").Status);
	}

	[Fact]
	public void Issue_SweepsExpiredReceiptsBeforeCapacityCheck()
	{
		var clock = new ManualTimeProvider();
		var store = new LocalPriceReviewReceiptStore(clock);
		for (var index = 0; index < LocalPriceReviewReceiptStore.MaximumReceiptsPerIdentity; index++)
		{
			Assert.Equal(PriceReviewReceiptIssueStatus.Issued, Issue(store).Status);
		}

		clock.Advance(LocalPriceReviewReceiptStore.ReceiptLifetime);

		Assert.Equal(PriceReviewReceiptIssueStatus.Issued, Issue(store).Status);
	}

	[Fact]
	public void ConcurrentIssueAndCheckCallsRemainAtomic()
	{
		var store = new LocalPriceReviewReceiptStore();
		var issueResults = new PriceReviewReceiptIssueResult[100];
		Parallel.For(0, issueResults.Length, index => issueResults[index] = Issue(store));

		Assert.Equal(
			LocalPriceReviewReceiptStore.MaximumReceiptsPerIdentity,
			issueResults.Count(result => result.Status == PriceReviewReceiptIssueStatus.Issued));
		Assert.All(issueResults.Where(result => result.Status != PriceReviewReceiptIssueStatus.Issued), result =>
		{
			Assert.Equal(PriceReviewReceiptIssueStatus.CapacityExceededRetryable, result.Status);
			Assert.Null(result.Handle);
		});

		var handles = issueResults.Where(result => result.Handle is not null).Select(result => result.Handle!).ToArray();
		var statuses = new PriceReviewReceiptCheckStatus[handles.Length * 4];
		Parallel.For(0, statuses.Length, index =>
			statuses[index] = Check(store, handles[index % handles.Length]).Status);
		Assert.All(statuses, status => Assert.Equal(PriceReviewReceiptCheckStatus.Current, status));
	}

	private static PriceReviewReceiptIssueResult Issue(
		LocalPriceReviewReceiptStore store,
		string issuer = Issuer,
		string subject = Subject,
		ProductListPriceSnapshot? prices = null) =>
		store.Issue(issuer, subject, Draft(), prices ?? Snapshot((7, 12.5m)));

	private static PriceReviewReceiptCheckResult Check(
		LocalPriceReviewReceiptStore store,
		string handle,
		string issuer = Issuer,
		string subject = Subject,
		ProductListPriceSnapshot? fresh = null) =>
		store.Check(handle, issuer, subject, Draft(), fresh ?? Snapshot((7, 12.5m)));

	private static ValidatedOrderDraftShape Draft(int quantity = 1) => new(
		CustomerId: 42,
		BillToAddressId: 11,
		ShipToAddressId: 12,
		ShipMethodId: 2,
		Lines: [new ValidatedOrderDraftLineShape(7, quantity, null)]);

	private static ProductListPriceSnapshot Snapshot(params (int ProductId, decimal? ListPrice)[] entries) =>
		new(true, entries.Select(entry => new ProductListPriceSnapshotEntry(entry.ProductId, entry.ListPrice)));

	private sealed class ManualTimeProvider : TimeProvider
	{
		private long timestamp;

		public override long TimestampFrequency => TimeSpan.TicksPerSecond;

		public override long GetTimestamp() => timestamp;

		public void Advance(TimeSpan amount) => timestamp += amount.Ticks;
	}
}