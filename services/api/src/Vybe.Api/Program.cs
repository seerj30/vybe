using Vybe.Api;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddVybeApi(builder.Configuration, builder.Environment);

var app = builder.Build();
app.UseVybeApi();
await app.SeedContentIfConfiguredAsync();
await app.RunAsync();

/// <summary>Entry point; public for WebApplicationFactory in the integration tests.</summary>
public partial class Program;
