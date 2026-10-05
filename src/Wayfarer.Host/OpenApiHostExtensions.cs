using Microsoft.OpenApi.Models;

namespace Wayfarer;

internal static class OpenApiUi
{
    public static bool Expose(IHostEnvironment environment, IConfiguration configuration) =>
        environment.IsDevelopment() || configuration.GetValue("OpenApi:ExposeUi", false);
}

internal static class WayfarerOpenApiHostExtensions
{
    public static WebApplicationBuilder AddWayfarerOpenApi(this WebApplicationBuilder builder)
    {
        builder.Services.AddOpenApi("v1", options =>
        {
            options.AddDocumentTransformer((document, _, _) =>
            {
                FailClosedOpenApiDocument.Apply(document);
                return Task.CompletedTask;
            });
        });

        return builder;
    }

    public static WebApplication UseWayfarerOpenApi(this WebApplication app)
    {
        app.MapOpenApi().AllowAnonymous();

        if (OpenApiUi.Expose(app.Environment, app.Configuration))
        {
            app.UseSwaggerUI(options =>
            {
                options.RoutePrefix = "swagger";
                options.DocumentTitle = "Wayfarer";
                options.SwaggerEndpoint("/openapi/v1.json", "Wayfarer v1");
                options.InjectStylesheet("/assets/swagger.css");
                options.ConfigObject.ValidatorUrl = null;
            });
        }

        return app;
    }
}

internal static class FailClosedOpenApiDocument
{
    public static void Apply(OpenApiDocument document)
    {
        document.Info = new OpenApiInfo
        {
            Title = "Wayfarer",
            Version = "v1",
            Description =
                "Fail-closed remittance host. Live payout is not implemented and the payout operation does not move money. "
                + "HTTP Bearer is declared for JWT access tokens. The routes in this document do not require a token."
        };

        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, OpenApiSecurityScheme>();
        document.Components.SecuritySchemes["Bearer"] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
            BearerFormat = "JWT",
            Description = "HTTP Bearer authentication with a JWT access token."
        };

        document.SecurityRequirements =
        [
            new OpenApiSecurityRequirement(),
            new OpenApiSecurityRequirement
            {
                [new OpenApiSecurityScheme
                {
                    Reference = new OpenApiReference
                    {
                        Type = ReferenceType.SecurityScheme,
                        Id = "Bearer"
                    }
                }] = []
            }
        ];

        document.Servers = [new OpenApiServer { Url = "/" }];

        if (document.Paths.TryGetValue("/api/remittance/payout", out var payout)
            && payout.Operations is not null
            && payout.Operations.TryGetValue(OperationType.Post, out var operation)
            && operation.Responses.ContainsKey("503"))
        {
            foreach (var key in operation.Responses.Keys.Where(key => key != "503").ToArray())
            {
                operation.Responses.Remove(key);
            }
        }
    }
}
