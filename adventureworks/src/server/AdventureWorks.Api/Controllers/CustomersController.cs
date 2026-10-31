using AdventureWorks.Api.Contracts;
using AdventureWorks.Api.Data;
using Microsoft.AspNetCore.Mvc;

namespace AdventureWorks.Api.Controllers;

[ApiController]
[Route("api/customers")]
public sealed class CustomersController(ICustomerReadStore customers) : ControllerBase
{
	[HttpGet]
	[ProducesResponseType<CustomerPageDto>(StatusCodes.Status200OK)]
	[ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
	[ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
	public async Task<IActionResult> Search([FromQuery] CustomerSearchRequest request, CancellationToken cancellationToken)
	{
		if (!customers.IsConfigured)
		{
			return CustomerDataUnavailable();
		}

		try
		{
			return Ok(await customers.SearchAsync(request, cancellationToken));
		}
		catch (SqlReadAdapterUnavailableException)
		{
			return CustomerDataUnavailable();
		}
	}

	[HttpGet("{customerId:int}/order-options")]
	[ProducesResponseType<CustomerOrderOptionsDto>(StatusCodes.Status200OK)]
	[ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
	[ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
	[ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
	public async Task<IActionResult> GetOrderOptions(int customerId, CancellationToken cancellationToken)
	{
		if (customerId <= 0)
		{
			return BadRequest(new ProblemDetails
			{
				Status = StatusCodes.Status400BadRequest,
				Title = "Invalid customer ID",
				Detail = "Customer ID must be a positive integer."
			});
		}

		if (!customers.IsConfigured)
		{
			return OrderOptionsDataUnavailable();
		}

		try
		{
			var options = await customers.GetOrderOptionsAsync(customerId, cancellationToken);
			return options is null
				? NotFound(new ProblemDetails
				{
					Status = StatusCodes.Status404NotFound,
					Title = "Customer not found",
					Detail = "No customer exists with the requested ID."
				})
				: Ok(options);
		}
		catch (SqlReadAdapterUnavailableException)
		{
			return OrderOptionsDataUnavailable();
		}
	}

	private ObjectResult OrderOptionsDataUnavailable() => StatusCode(StatusCodes.Status503ServiceUnavailable, new ProblemDetails
	{
		Status = StatusCodes.Status503ServiceUnavailable,
		Title = "Customer order options unavailable",
		Detail = "The approved read-only AdventureWorks source or required order-options permissions are unavailable."
	});

	private ObjectResult CustomerDataUnavailable() => StatusCode(StatusCodes.Status503ServiceUnavailable, new ProblemDetails
	{
		Status = StatusCodes.Status503ServiceUnavailable,
		Title = "Customer data source unavailable",
		Detail = "Configure an approved read-only AdventureWorks principal and customer query adapter, and approve customer visibility policy, before enabling customer reads."
	});
}