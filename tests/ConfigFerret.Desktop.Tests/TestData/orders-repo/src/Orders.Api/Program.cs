using Orders.Api;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddEnvironmentVariables("ORDERS_");

builder.Services.Configure<DbOptions>(builder.Configuration.GetSection(DbOptions.SectionName));
builder.Services.AddOptions<SmtpOptions>().BindConfiguration("Smtp");

var cacheTtl = builder.Configuration.GetValue<int>("Cache:Ttl");
var searchEnabled = builder.Configuration["Features:Search"];
var apiBaseUrl = builder.Configuration.GetSection("Api")["BaseUrl"];

// Payments moved to the payments service; this used to read Payments:ApiKey.
// var paymentsKey = builder.Configuration["Payments:ApiKey"];

var app = builder.Build();
app.MapGet("/", () => $"orders {cacheTtl} {searchEnabled} {apiBaseUrl}");
app.Run();
