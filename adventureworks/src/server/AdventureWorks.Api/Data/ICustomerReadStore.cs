using AdventureWorks.Api.Contracts;

namespace AdventureWorks.Api.Data;

public interface ICustomerReadStore
{
	bool IsConfigured { get; }

	Task<CustomerPageDto> SearchAsync(CustomerSearchRequest request, CancellationToken cancellationToken);

	Task<CustomerOrderOptionsDto?> GetOrderOptionsAsync(int customerId, CancellationToken cancellationToken);
}

public sealed class UnconfiguredCustomerReadStore : ICustomerReadStore
{
	public bool IsConfigured => false;

	public Task<CustomerPageDto> SearchAsync(CustomerSearchRequest request, CancellationToken cancellationToken) =>
		throw new InvalidOperationException("No approved read-only AdventureWorks customer store is configured.");

	public Task<CustomerOrderOptionsDto?> GetOrderOptionsAsync(int customerId, CancellationToken cancellationToken) =>
		throw new InvalidOperationException("No approved read-only AdventureWorks customer store is configured.");
}