using AdventureWorks.Api.Contracts;
using AdventureWorks.Api.Application;
using AdventureWorks.Api.Data;
using Microsoft.AspNetCore.Mvc;

namespace AdventureWorks.Api.Controllers;

[ApiController]
[Route("api/products")]
public sealed class ProductsController(IProductCatalogReadStore catalog, TimeProvider timeProvider) : ControllerBase
{
	[HttpGet]
	[ProducesResponseType<ProductPageDto>(StatusCodes.Status200OK)]
	[ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
	[ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
	public async Task<IActionResult> Search([FromQuery] ProductSearchRequest request, CancellationToken cancellationToken)
	{
		if (!catalog.IsConfigured)
		{
			return CatalogUnavailable();
		}

		try
		{
			return Ok(await catalog.SearchAsync(request, cancellationToken));
		}
		catch (SqlReadAdapterUnavailableException)
		{
			return CatalogUnavailable();
		}
	}

	[HttpGet("{productId:int}")]
	[ProducesResponseType<ProductDetailDto>(StatusCodes.Status200OK)]
	[ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
	[ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
	[ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
	public async Task<IActionResult> GetById(int productId, CancellationToken cancellationToken)
	{
		if (productId <= 0)
		{
			return BadRequest(new ProblemDetails
			{
				Status = StatusCodes.Status400BadRequest,
				Title = "Invalid product ID",
				Detail = "Product ID must be a positive integer."
			});
		}

		if (!catalog.IsConfigured)
		{
			return CatalogUnavailable();
		}

		try
		{
			var product = await catalog.GetByIdAsync(productId, cancellationToken);
			return product is null ? NotFound() : Ok(product);
		}
		catch (SqlReadAdapterUnavailableException)
		{
			return CatalogUnavailable();
		}
	}

	[HttpGet("{productId:int}/offers")]
	[ProducesResponseType<IReadOnlyList<ProductOfferDto>>(StatusCodes.Status200OK)]
	[ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
	[ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
	public async Task<IActionResult> GetOffers(int productId, CancellationToken cancellationToken)
	{
		if (productId <= 0)
		{
			return BadRequest(new ProblemDetails
			{
				Status = StatusCodes.Status400BadRequest,
				Title = "Invalid product ID",
				Detail = "Product ID must be a positive integer."
			});
		}

		if (!catalog.IsConfigured)
		{
			return CatalogUnavailable();
		}

		try
		{
			return Ok(await catalog.GetOffersAsync(productId, cancellationToken));
		}
		catch (SqlReadAdapterUnavailableException)
		{
			return CatalogUnavailable();
		}
	}

	[HttpGet("{productId:int}/eligible-offers")]
	[ProducesResponseType<IReadOnlyList<ProductEligibleOfferDto>>(StatusCodes.Status200OK)]
	[ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
	[ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
	[ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
	public async Task<IActionResult> GetEligibleOffers(
		int productId,
		[FromQuery] int quantity,
		CancellationToken cancellationToken)
	{
		if (productId <= 0)
		{
			return BadRequest(new ProblemDetails
			{
				Status = StatusCodes.Status400BadRequest,
				Title = "Invalid product ID",
				Detail = "Product ID must be a positive integer."
			});
		}

		if (quantity is < 1 or > short.MaxValue)
		{
			return BadRequest(new ProblemDetails
			{
				Status = StatusCodes.Status400BadRequest,
				Title = "Invalid quantity",
				Detail = "Quantity must be an integer from 1 through 32767."
			});
		}

		if (!catalog.IsConfigured)
		{
			return CatalogUnavailable();
		}

		try
		{
			var eligibilityData = await catalog.GetOfferEligibilityDataAsync(productId, cancellationToken);
			if (eligibilityData is null)
			{
				return NotFound();
			}

			var evaluationInstant = timeProvider.GetUtcNow();
			var eligibleOffers = eligibilityData.Offers
				.Where(offer => OfferEligibilityEvaluator.IsEligible(
					offer.StartDate,
					offer.EndDate,
					evaluationInstant,
					quantity,
					offer.MinQty,
					offer.MaxQty,
					isProductLinked: true))
				.Select(offer => new ProductEligibleOfferDto(offer.SpecialOfferId, offer.Description, offer.DiscountPct))
				.ToArray();

			return Ok(eligibleOffers);
		}
		catch (SqlReadAdapterUnavailableException)
		{
			return CatalogUnavailable();
		}
	}

	[HttpGet("{productId:int}/no-offer-persistence")]
	[ProducesResponseType<ProductNoOfferPersistenceDto>(StatusCodes.Status200OK)]
	[ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
	[ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
	[ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
	public async Task<IActionResult> GetNoOfferPersistenceSupport(int productId, CancellationToken cancellationToken)
	{
		if (productId <= 0)
		{
			return BadRequest(new ProblemDetails
			{
				Status = StatusCodes.Status400BadRequest,
				Title = "Invalid product ID",
				Detail = "Product ID must be a positive integer."
			});
		}

		if (!catalog.IsConfigured)
		{
			return CatalogUnavailable();
		}

		try
		{
			var isSupported = await catalog.GetNoOfferPersistenceSupportAsync(productId, cancellationToken);
			return isSupported is null
				? NotFound()
				: Ok(new ProductNoOfferPersistenceDto(isSupported.Value));
		}
		catch (SqlReadAdapterUnavailableException)
		{
			return CatalogUnavailable();
		}
	}

	private ObjectResult CatalogUnavailable() => StatusCode(StatusCodes.Status503ServiceUnavailable, new ProblemDetails
	{
		Status = StatusCodes.Status503ServiceUnavailable,
		Title = "Catalog data source unavailable",
		Detail = "Configure an approved read-only AdventureWorks principal and catalog query adapter before enabling catalog reads."
	});
}