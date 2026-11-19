using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;
using Whatsapp.Api.Endpoints;
using Whatsapp.Api.Health;
using Whatsapp.Api.Infrastructure;
using Whatsapp.Api.Security;
using Whatsapp.CloudApi;
using Whatsapp.Events;
using Whatsapp.Outbox;
using Whatsapp.Templates;
using Whatsapp.Webhooks;

namespace Whatsapp.Api;

public static class WhatsappApiExtensions
{
    public const string OpenApiDocumentName = "v1";

    public const string ReadyTag = "ready";

    public static IServiceCollection AddWhatsappApi(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);

        services.TryAddSingleton(TimeProvider.System);

        // Domain modules.
        services.AddWhatsappCloudApi(configuration);
        services.AddWhatsappTemplates(configuration);

        services.AddOptions<DatabaseOptions>()
            .Bind(configuration.GetSection(DatabaseOptions.SectionName))
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<DatabaseOptions>, DatabaseOptionsValidator>());
        services.AddWhatsappOutbox(
            configuration,
            (provider, builder) => provider.GetRequiredService<IOptions<DatabaseOptions>>().Value.Configure(builder));

        services.AddWhatsappEvents(configuration);
        services.AddWhatsappWebhooks(configuration);

        // Authentication and authorisation.
        services.AddOptions<ApiAuthOptions>()
            .Bind(configuration.GetSection(ApiAuthOptions.SectionName))
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<ApiAuthOptions>, ApiAuthOptionsValidator>());
        services.AddSingleton<ApiKeyStore>();
        services.AddAuthentication(ApiKeyAuthenticationHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(ApiKeyAuthenticationHandler.SchemeName, _ => { });
        services.AddAuthorizationBuilder()
            .SetFallbackPolicy(new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .Build())
            .AddPolicy(ApiPolicies.AnyKey, policy => policy.RequireAuthenticatedUser())
            .AddPolicy(ApiPolicies.TenantKey, policy => policy.RequireRole(ApiRoles.Tenant))
            .AddPolicy(ApiPolicies.AdminKey, policy => policy.RequireRole(ApiRoles.Admin));

        // Errors.
        services.AddExceptionHandler<ApiExceptionHandler>();
        services.AddProblemDetails(options => options.CustomizeProblemDetails = context =>
        {
            if (context.HttpContext.Items.TryGetValue(CorrelationIdMiddleware.ItemKey, out var correlationId))
            {
                context.ProblemDetails.Extensions["correlationId"] = correlationId;
            }
        });

        services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
            options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
            options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        });

        services.AddOpenApi(OpenApiDocumentName, options =>
        {
            options.AddDocumentTransformer((document, _, _) =>
            {
                document.Info = new OpenApiInfo
                {
                    Title = "WhatsApp Cloud Notifications API",
                    Version = "1.0.0",
                    Description = "Transactional WhatsApp notifications with an outbox, per-tenant credentials and service-window handling.",
                };

                // The server list would otherwise carry the host the document was generated on.
                document.Servers = [];

                document.Components ??= new OpenApiComponents();
                document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
                document.Components.SecuritySchemes["ApiKey"] = new OpenApiSecurityScheme
                {
                    Type = SecuritySchemeType.ApiKey,
                    In = ParameterLocation.Header,
                    Name = ApiKeyAuthenticationHandler.HeaderName,
                    Description = "Tenant API key, or the admin key for admin routes.",
                };
                document.Security =
                [
                    new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference("ApiKey", document)] = [] },
                ];
                return Task.CompletedTask;
            });
        });

        // Health.
        services.AddHealthChecks()
            .AddDbContextCheck<OutboxDbContext>("database", tags: [ReadyTag])
            .AddCheck<OutboxDispatcherHealthCheck>("dispatcher", tags: [ReadyTag]);

        return services;
    }

    public static void ConfigureWhatsappLogging(this ILoggingBuilder logging, IHostEnvironment environment)
    {
        // The host logs every request line with its full URL, query string included, and the webhook handshake carries
        // the verify token in the query string. That one category is held at Warning so a token never reaches a log;
        // every other category keeps its default level.
        logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning);

        // Structured JSON logs for deployed environments; the default console format elsewhere.
        if (!environment.IsProduction())
        {
            return;
        }

        logging.ClearProviders();
        logging.AddJsonConsole(options => options.IncludeScopes = true);
    }

    public static WebApplication UseWhatsappApi(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.UseMiddleware<CorrelationIdMiddleware>();
        app.UseExceptionHandler();
        app.UseStatusCodePages();
        app.UseAuthentication();
        app.UseAuthorization();

        app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false }).AllowAnonymous();
        app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains(ReadyTag) }).AllowAnonymous();

        // The contract is served behind the same API key as everything else.
        app.MapOpenApi("/openapi/{documentName}.json");

        var api = app.MapGroup("/api/v1");
        api.MapNotificationEndpoints();
        api.MapEventEndpoints();
        api.MapAdminEndpoints();
        api.MapWebhookEndpoints();

        return app;
    }
}
