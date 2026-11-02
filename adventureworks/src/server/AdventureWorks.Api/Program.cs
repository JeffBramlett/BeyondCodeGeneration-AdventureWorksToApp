using AdventureWorks.Api;
using AdventureWorks.Api.Application;
using AdventureWorks.Api.Data;
using AdventureWorks.Api.Authentication;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(ApiRequestBodyLimit.ConfigureKestrel);

ApiAuthentication.Configure(builder);
builder.Services.AddControllers();
builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
var sqlConfiguration = SqlServerReadAdapterConfiguration.FromEnvironment();
builder.Services.AddSingleton(sqlConfiguration);
if (sqlConfiguration.IsConfigured)
{
	builder.Services.AddSingleton<SqlServerReadStore>();
	builder.Services.AddSingleton<IProductCatalogReadStore>(services => services.GetRequiredService<SqlServerReadStore>());
	builder.Services.AddSingleton<ICustomerReadStore>(services => services.GetRequiredService<SqlServerReadStore>());
	builder.Services.AddSingleton<IOrderReadStore>(services => services.GetRequiredService<SqlServerReadStore>());
	builder.Services.AddSingleton<IOfferPricingReadStore>(services => services.GetRequiredService<SqlServerReadStore>());
}
else
{
	builder.Services.AddSingleton<IProductCatalogReadStore, UnconfiguredProductCatalogReadStore>();
	builder.Services.AddSingleton<ICustomerReadStore, UnconfiguredCustomerReadStore>();
	builder.Services.AddSingleton<IOrderReadStore, UnconfiguredOrderReadStore>();
	builder.Services.AddSingleton<IOfferPricingReadStore, UnconfiguredOfferPricingReadStore>();
}

// Local-demo order submission (Project Owner decision 2026-10-30; see docs/api/order-submission.md).
var orderWriterConfiguration = SqlServerOrderWriterConfiguration.FromEnvironment();
builder.Services.AddSingleton(orderWriterConfiguration);
if (orderWriterConfiguration.IsConfigured)
{
	builder.Services.AddSingleton<IOrderWriteStore, SqlServerOrderWriteStore>();
}
else
{
	builder.Services.AddSingleton<IOrderWriteStore, UnconfiguredOrderWriteStore>();
}

builder.Services.AddSingleton<OrderSubmissionTokenRegistry>();
builder.Services.AddTransient<OrderSubmissionService>();

var app = builder.Build();

app.UseHttpsRedirection();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapGet("/api/health", () => Results.Ok(new { status = "ok" }))
	.AllowAnonymous();
app.Map("/api/{**path}", () => Results.NotFound())
	.RequireAuthorization();

app.MapStaticAssets()
	.RequireAuthorization("InternalStaff");
app.MapFallbackToFile("index.html")
	.RequireAuthorization("InternalStaff");

app.Run();

public partial class Program;
