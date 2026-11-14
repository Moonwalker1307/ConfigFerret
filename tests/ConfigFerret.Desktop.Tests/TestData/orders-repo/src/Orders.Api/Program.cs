var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddEnvironmentVariables("ORDERS_");
var app = builder.Build();
app.MapGet("/", () => "orders");
app.Run();
