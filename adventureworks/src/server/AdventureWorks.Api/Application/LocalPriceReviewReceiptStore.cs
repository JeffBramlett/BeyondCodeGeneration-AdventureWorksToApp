using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using AdventureWorks.Api.Contracts;

namespace AdventureWorks.Api.Application;

internal enum PriceReviewReceiptIssueStatus
{
	Issued,
	InvalidInput,
	PayloadTooLarge,
	CapacityExceededRetryable
}

internal sealed class PriceReviewReceiptIssueResult
{
	public PriceReviewReceiptIssueStatus Status { get; }
	public string? Handle { get; }

	private PriceReviewReceiptIssueResult(PriceReviewReceiptIssueStatus status, string? handle)
	{
		Status = status;
		Handle = handle;
	}

	public static PriceReviewReceiptIssueResult Issued(string handle) =>
		new(PriceReviewReceiptIssueStatus.Issued, handle);

	public static PriceReviewReceiptIssueResult Failed(PriceReviewReceiptIssueStatus status) =>
		new(status, null);
}

internal enum PriceReviewReceiptCheckStatus
{
	Current,
	ReviewRequired,
	PriceSourceUnavailable,
	InvalidReceipt
}

internal sealed class PriceReviewReceiptCheckResult(PriceReviewReceiptCheckStatus status)
{
	public PriceReviewReceiptCheckStatus Status { get; } = status;
}

internal sealed class LocalPriceReviewReceiptStore
{
	internal static LocalPriceReviewReceiptStore ProcessInstance { get; } = new();

	internal static readonly TimeSpan ReceiptLifetime = TimeSpan.FromMinutes(15);
	internal const int MaximumReceiptsPerIdentity = 20;
	internal const int MaximumReceipts = 500;
	internal const int MaximumPayloadBytes = 16 * 1024;
	internal const long MaximumAggregatePayloadBytes = 8 * 1024 * 1024;

	private const int PayloadVersion = 1;
	private readonly object gate = new();
	private readonly TimeProvider timeProvider;
	private readonly byte[] digestKey = RandomNumberGenerator.GetBytes(32);
	private readonly Dictionary<string, ReceiptEntry> receipts = new(StringComparer.Ordinal);
	private long aggregatePayloadBytes;

	internal LocalPriceReviewReceiptStore(TimeProvider? timeProvider = null)
	{
		this.timeProvider = timeProvider ?? TimeProvider.System;
	}

	internal PriceReviewReceiptIssueResult Issue(
		string issuer,
		string stableSubject,
		ValidatedOrderDraftShape draft,
		ProductListPriceSnapshot reviewedPrices)
	{
		if (string.IsNullOrWhiteSpace(issuer)
			|| string.IsNullOrWhiteSpace(stableSubject)
			|| !IsValidDraft(draft)
			|| !IsCompleteKnownSnapshot(draft, reviewedPrices))
		{
			return PriceReviewReceiptIssueResult.Failed(PriceReviewReceiptIssueStatus.InvalidInput);
		}

		var fingerprint = CanonicalOrderRequestFingerprint.Compute(draft);
		var snapshot = CopySnapshot(reviewedPrices);
		var payload = SerializeCanonicalPayload(fingerprint, snapshot);
		if (payload.Length > MaximumPayloadBytes)
		{
			return PriceReviewReceiptIssueResult.Failed(PriceReviewReceiptIssueStatus.PayloadTooLarge);
		}

		lock (gate)
		{
			var now = timeProvider.GetTimestamp();
			SweepExpired(now);
			var identityCount = receipts.Values.Count(receipt =>
				string.Equals(receipt.Issuer, issuer, StringComparison.Ordinal)
				&& string.Equals(receipt.StableSubject, stableSubject, StringComparison.Ordinal));
			if (identityCount >= MaximumReceiptsPerIdentity
				|| receipts.Count >= MaximumReceipts
				|| aggregatePayloadBytes + payload.Length > MaximumAggregatePayloadBytes)
			{
				return PriceReviewReceiptIssueResult.Failed(PriceReviewReceiptIssueStatus.CapacityExceededRetryable);
			}

			var rawHandle = RandomNumberGenerator.GetBytes(32);
			var handle = EncodeHandle(rawHandle);
			var digest = ComputeHandleDigest(rawHandle);
			while (receipts.ContainsKey(digest))
			{
				CryptographicOperations.ZeroMemory(rawHandle);
				rawHandle = RandomNumberGenerator.GetBytes(32);
				handle = EncodeHandle(rawHandle);
				digest = ComputeHandleDigest(rawHandle);
			}

			receipts.Add(digest, new ReceiptEntry(
				issuer,
				stableSubject,
				fingerprint,
				snapshot,
				payload,
				now,
				payload.Length));
			aggregatePayloadBytes += payload.Length;
			CryptographicOperations.ZeroMemory(rawHandle);
			return PriceReviewReceiptIssueResult.Issued(handle);
		}
	}

	internal PriceReviewReceiptCheckResult Check(
		string? handle,
		string issuer,
		string stableSubject,
		ValidatedOrderDraftShape draft,
		ProductListPriceSnapshot? freshPrices)
	{
		if (!TryDecodeHandle(handle, out var rawHandle))
		{
			return CheckResult(PriceReviewReceiptCheckStatus.InvalidReceipt);
		}

		var digest = ComputeHandleDigest(rawHandle);
		CryptographicOperations.ZeroMemory(rawHandle);
		if (string.IsNullOrWhiteSpace(issuer)
			|| string.IsNullOrWhiteSpace(stableSubject)
			|| !IsValidDraft(draft))
		{
			return CheckResult(PriceReviewReceiptCheckStatus.InvalidReceipt);
		}

		var fingerprint = CanonicalOrderRequestFingerprint.Compute(draft);
		var now = timeProvider.GetTimestamp();
		lock (gate)
		{
			if (!receipts.TryGetValue(digest, out var receipt))
			{
				return CheckResult(PriceReviewReceiptCheckStatus.InvalidReceipt);
			}

			if (timeProvider.GetElapsedTime(receipt.IssuedAtTimestamp, now) >= ReceiptLifetime)
			{
				Remove(digest, receipt);
				return CheckResult(PriceReviewReceiptCheckStatus.InvalidReceipt);
			}

			if (!string.Equals(receipt.Issuer, issuer, StringComparison.Ordinal)
				|| !string.Equals(receipt.StableSubject, stableSubject, StringComparison.Ordinal))
			{
				return CheckResult(PriceReviewReceiptCheckStatus.InvalidReceipt);
			}

			if (!string.Equals(receipt.DraftFingerprint, fingerprint, StringComparison.Ordinal))
			{
				Remove(digest, receipt);
				return CheckResult(PriceReviewReceiptCheckStatus.InvalidReceipt);
			}

			if (freshPrices is null || (!freshPrices.IsAvailable && freshPrices.Entries.Count == 0))
			{
				return CheckResult(PriceReviewReceiptCheckStatus.PriceSourceUnavailable);
			}

			var review = ProductListPriceReviewEvaluator.Evaluate(receipt.ReviewedPrices, freshPrices);
			switch (review.Status)
			{
				case ProductListPriceReviewStatus.Current:
					return CheckResult(PriceReviewReceiptCheckStatus.Current);
				case ProductListPriceReviewStatus.ReviewRequired:
					Remove(digest, receipt);
					return CheckResult(PriceReviewReceiptCheckStatus.ReviewRequired);
				default:
					Remove(digest, receipt);
					return CheckResult(PriceReviewReceiptCheckStatus.InvalidReceipt);
			}
		}
	}

	private void SweepExpired(long nowTimestamp)
	{
		foreach (var (digest, receipt) in receipts.ToArray())
		{
			if (timeProvider.GetElapsedTime(receipt.IssuedAtTimestamp, nowTimestamp) >= ReceiptLifetime)
			{
				Remove(digest, receipt);
			}
		}
	}

	private void Remove(string digest, ReceiptEntry receipt)
	{
		if (receipts.Remove(digest))
		{
			aggregatePayloadBytes -= receipt.PayloadBytes;
		}
	}

	private static bool IsValidDraft(ValidatedOrderDraftShape? draft)
	{
		if (draft is null
			|| draft.CustomerId <= 0
			|| draft.BillToAddressId <= 0
			|| draft.ShipToAddressId <= 0
			|| draft.ShipMethodId <= 0
			|| draft.Lines.Count is < 1 or > OrderDraftRequestValidator.MaximumLineCount)
		{
			return false;
		}

		var productIds = new HashSet<int>();
		return draft.Lines.All(line =>
			line is not null
			&& line.ProductId > 0
			&& productIds.Add(line.ProductId)
			&& line.Quantity is >= 1 and <= short.MaxValue
			&& line.OfferId is not <= 0);
	}

	private static bool IsCompleteKnownSnapshot(
		ValidatedOrderDraftShape draft,
		ProductListPriceSnapshot? snapshot)
	{
		if (snapshot is null || !snapshot.IsAvailable || snapshot.Entries.Count != draft.Lines.Count)
		{
			return false;
		}

		var expectedProductIds = draft.Lines.Select(line => line.ProductId).ToHashSet();
		foreach (var entry in snapshot.Entries)
		{
			if (entry is null
				|| entry.ProductId <= 0
				|| entry.ListPrice is null or < 0m
				|| !expectedProductIds.Remove(entry.ProductId))
			{
				return false;
			}
		}

		return expectedProductIds.Count == 0;
	}

	private static ProductListPriceSnapshot CopySnapshot(ProductListPriceSnapshot snapshot) =>
		new(true, snapshot.Entries.Select(entry => new ProductListPriceSnapshotEntry(entry!.ProductId, entry.ListPrice)));

	private static byte[] SerializeCanonicalPayload(string fingerprint, ProductListPriceSnapshot snapshot)
	{
		using var stream = new MemoryStream();
		using (var writer = new Utf8JsonWriter(stream))
		{
			writer.WriteStartObject();
			writer.WriteNumber("payloadVersion", PayloadVersion);
			writer.WriteString("draftFingerprint", fingerprint);
			writer.WriteStartArray("prices");
			foreach (var entry in snapshot.Entries.OrderBy(entry => entry!.ProductId))
			{
				writer.WriteStartObject();
				writer.WriteNumber("productId", entry!.ProductId);
				writer.WritePropertyName("listPrice");
				writer.WriteRawValue(entry.ListPrice!.Value.ToString("G29", CultureInfo.InvariantCulture));
				writer.WriteEndObject();
			}

			writer.WriteEndArray();
			writer.WriteEndObject();
		}

		return stream.ToArray();
	}

	private string ComputeHandleDigest(byte[] rawHandle) =>
		Convert.ToHexString(HMACSHA256.HashData(digestKey, rawHandle));

	private string EncodeHandle(byte[] rawHandle) =>
		Convert.ToBase64String(rawHandle).TrimEnd('=').Replace('+', '-').Replace('/', '_');

	private static bool TryDecodeHandle(string? handle, out byte[] rawHandle)
	{
		rawHandle = [];
		if (handle is null || handle.Length != 43 || handle.Any(character =>
			!(character is >= 'A' and <= 'Z'
				or >= 'a' and <= 'z'
				or >= '0' and <= '9'
				or '-' or '_')))
		{
			return false;
		}

		try
		{
			var base64 = handle.Replace('-', '+').Replace('_', '/') + "=";
			rawHandle = Convert.FromBase64String(base64);
			return rawHandle.Length == 32 && string.Equals(
				Convert.ToBase64String(rawHandle).TrimEnd('=').Replace('+', '-').Replace('/', '_'),
				handle,
				StringComparison.Ordinal);
		}
		catch (FormatException)
		{
			return false;
		}
	}

	private static PriceReviewReceiptCheckResult CheckResult(PriceReviewReceiptCheckStatus status) => new(status);

	private sealed record ReceiptEntry(
		string Issuer,
		string StableSubject,
		string DraftFingerprint,
		ProductListPriceSnapshot ReviewedPrices,
		byte[] CanonicalPayload,
		long IssuedAtTimestamp,
		int PayloadBytes);
}