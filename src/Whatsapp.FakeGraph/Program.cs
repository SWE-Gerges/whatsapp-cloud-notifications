using Whatsapp.FakeGraph;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddFakeGraph(builder.Configuration);

var app = builder.Build();
app.MapFakeGraph();

await app.RunAsync();
