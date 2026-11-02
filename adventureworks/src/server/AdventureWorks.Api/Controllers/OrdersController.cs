using AdventureWorks.Api.Application;
using AdventureWorks.Api.Contracts;
using AdventureWorks.Api.Data;
using Microsoft.AspNetCore.Mvc;

namespace AdventureWorks.Api.Controllers;

[ApiController]
[Route("api/orders")]
public sealed class OrdersController(IOrderReadStore orders) : ControllerBase
{
	[HttpGet]
	[ProducesResponseType<OrderPageDto>(StatusCodes.Status200OK)]
	[ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
	[ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
	public async Task<IActionResult> Search([FromQuery] OrderSearchRequest request, CancellationToken cancellationToken)
	{
		if (!orders.IsConfigured)
		{
			return OrderDataUnavailable();
		}

		try
		{
			return Ok(await orders.SearchAsync(request, cancellationToken));
		}
		catch (SqlReadAdapterUnavailableException)
		{
			return OrderDataUnavailable();
		}
	}

	[HttpGet("{orderReference}")]
	[ProducesResponseType<OrderDetailPageDto>(StatusCodes.Status200OK)]
	[ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
	[ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
	[ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
	public async Task<IActionResult> GetByReference(
		string orderReference,
		[FromQuery] OrderLinesRequest request,
		CancellationToken cancellationToken)
	{
		var normalizedReference = orderReference.Trim();
		if (normalizedReference.Length is < 1 or > 25)
		{
			return BadRequest(new ProblemDetails
			{
				Status = StatusCodes.Status400BadRequest,
				Title = "Invalid order reference",
				Detail = "Order reference must contain 1 to 25 non-whitespace characters."
			});
		}

		if (!orders.IsConfigured)
		{
			return OrderDataUnavailable();
		}

		try
		{
			var result = await orders.GetByReferenceAsync(normalizedReference, request, cancellationToken);
			return result is null
				? NotFound(new ProblemDetails
				{
					Status = StatusCodes.Status404NotFound,
					Title = "Order not found",
					Detail = "No order matches the requested reference."
				})
				: Ok(result);
		}
		catch (SqlReadAdapterUnavailableException)
		{
			return OrderDataUnavailable();
		}
	}

	[HttpPost]
	[ProducesResponseType<OrderSubmissionConfirmationDto>(StatusCodes.Status201Created)]
	[ProducesResponseType<OrderSubmissionConfirmationDto>(StatusCodes.Status200OK)]
	[ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
	[ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
	[ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
	[ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
	public async Task<IActionResult> Submit(
		[FromBody] OrderSubmissionRequest? request,
		[FromServices] OrderSubmissionService submissions,
		CancellationToken cancellationToken)
	{
		var result = await submissions.SubmitAsync(request, cancellationToken);
		return result.Outcome switch
		{
			OrderSubmissionOutcome.Created => Created(
				$"/api/orders/{Uri.EscapeDataString(result.Confirmation!.OrderReference)}",
				result.Confirmation),
			OrderSubmissionOutcome.Replayed => Ok(result.Confirmation),
			OrderSubmissionOutcome.InvalidShape => BadRequest(new ValidationProblemDetails(ToMutable(result.Errors))
			{
				Status = StatusCodes.Status400BadRequest,
				Title = "Invalid order submission",
				Detail = "Correct the highlighted fields and submit again."
			}),
			OrderSubmissionOutcome.InProgress => Conflict(new ProblemDetails
			{
				Status = StatusCodes.Status409Conflict,
				Title = "Submission already in progress",
				Detail = "This submission token is already being processed. Wait for the result before retrying."
			}),
			OrderSubmissionOutcome.RequiresReview => UnprocessableEntity(new ValidationProblemDetails(ToMutable(result.Errors))
			{
				Status = StatusCodes.Status422UnprocessableEntity,
				Title = "Order requires review",
				Detail = "Some selections are no longer valid. Review the highlighted fields and submit again."
			}),
			_ => StatusCode(StatusCodes.Status503ServiceUnavailable, new ProblemDetails
			{
				Status = StatusCodes.Status503ServiceUnavailable,
				Title = "Order submission unavailable",
				Detail = "The AdventureWorks test data source or demo order writer is unavailable. No order was recorded."
			})
		};
	}

	private static Dictionary<string, string[]> ToMutable(IReadOnlyDictionary<string, string[]> errors) =>
		errors.ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);

	private ObjectResult OrderDataUnavailable() => StatusCode(StatusCodes.Status503ServiceUnavailable, new ProblemDetails
	{
		Status = StatusCodes.Status503ServiceUnavailable,
		Title = "Order data source unavailable",
		Detail = "The approved read-only AdventureWorks test source or exact order-column permissions are unavailable."
	});
}
