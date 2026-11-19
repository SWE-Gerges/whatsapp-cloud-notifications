using Whatsapp.Api;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddWhatsappApi(builder.Configuration, builder.Environment);
builder.Logging.ConfigureWhatsappLogging(builder.Environment);

var app = builder.Build();

app.UseWhatsappApi();

await app.RunAsync();
