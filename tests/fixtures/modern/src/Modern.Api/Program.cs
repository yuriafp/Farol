using Modern.Core.Pricing;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddSingleton<PriceCalculator>();

var app = builder.Build();
app.MapPost("/orders/total", (decimal[] prices, PriceCalculator calculator) => calculator.Total(prices, 0.1m));
app.Run();
