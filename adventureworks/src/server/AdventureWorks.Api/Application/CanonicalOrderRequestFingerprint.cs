using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AdventureWorks.Api.Contracts;

namespace AdventureWorks.Api.Application;

public static class CanonicalOrderRequestFingerprint
{
	public const int Version = 1;
	private const string FingerprintDomain = "order-request:v1\n";

	public static string Canonicalize(ValidatedOrderDraftShape draft)
	{
		ArgumentNullException.ThrowIfNull(draft);

		using var stream = new MemoryStream();
		using (var writer = new Utf8JsonWriter(stream))
		{
			writer.WriteStartObject();
			writer.WriteNumber("canonicalizationVersion", Version);
			writer.WriteNumber("customerId", draft.CustomerId);
			writer.WriteNumber("billToAddressId", draft.BillToAddressId);
			writer.WriteNumber("shipToAddressId", draft.ShipToAddressId);
			writer.WriteNumber("shipMethodId", draft.ShipMethodId);
			writer.WriteStartArray("lines");
			foreach (var line in draft.Lines)
			{
				writer.WriteStartObject();
				writer.WriteNumber("productId", line.ProductId);
				writer.WriteNumber("quantity", line.Quantity);
				if (line.OfferId is int offerId)
				{
					writer.WriteNumber("offerId", offerId);
				}
				else
				{
					writer.WriteNull("offerId");
				}

				writer.WriteEndObject();
			}

			writer.WriteEndArray();
			writer.WriteEndObject();
		}

		return Encoding.UTF8.GetString(stream.ToArray());
	}

	public static string Compute(ValidatedOrderDraftShape draft)
	{
		var canonicalJson = Canonicalize(draft);
		var domainBytes = Encoding.UTF8.GetBytes(FingerprintDomain);
		var requestBytes = Encoding.UTF8.GetBytes(canonicalJson);
		var input = new byte[domainBytes.Length + requestBytes.Length];
		Buffer.BlockCopy(domainBytes, 0, input, 0, domainBytes.Length);
		Buffer.BlockCopy(requestBytes, 0, input, domainBytes.Length, requestBytes.Length);
		return Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();
	}
}