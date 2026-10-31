using AdventureWorks.Api.Contracts;

namespace AdventureWorks.Api.Data;

public interface IOrderReadStore
{
	bool IsConfigured { get; }

	Task<OrderPageDto> SearchAsync(OrderSearchRequest request, CancellationToken cancellationToken);

	Task<OrderDetailPageDto?> GetByReferenceAsync(
		string orderReference,
		OrderLinesRequest request,
		CancellationToken cancellationToken);
}

public sealed class UnconfiguredOrderReadStore : IOrderReadStore
{
	public bool IsConfigured => false;

	public Task<OrderPageDto> SearchAsync(OrderSearchRequest request, CancellationToken cancellationToken) =>
		throw new InvalidOperationException("No approved read-only AdventureWorks order store is configured.");

	public Task<OrderDetailPageDto?> GetByReferenceAsync(
		string orderReference,
		OrderLinesRequest request,
		CancellationToken cancellationToken) =>
		throw new InvalidOperationException("No approved read-only AdventureWorks order store is configured.");
}
