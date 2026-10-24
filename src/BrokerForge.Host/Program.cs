using BrokerForge.Host;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddBrokerForgeHost(builder.Configuration, builder.Environment);

var app = builder.Build();
app.MapBrokerForgeEndpoints();
app.Run();

/// <summary>Exposed for WebApplicationFactory in contract tests.</summary>
public partial class Program
{
}
