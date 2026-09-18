using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace PropertyGpsApi.Infrastructure.Security;

/// Declares the bearer scheme on the generated OpenAPI document.
///
/// Without this the Swagger UI has no Authorize button. That matters more here than in a
/// typical project: Program.cs sets a fallback authorization policy, so every endpoint
/// that does not say [AllowAnonymous] needs a token, and "Try it out" would return 401 on
/// all of them. The scheme is declared at document level so it applies to every operation
/// rather than being repeated per action.
internal sealed class OpenApiJwtTransformer : IOpenApiDocumentTransformer
{
    private const string SchemeName = "Bearer";

    public Task TransformAsync(
        OpenApiDocument document,
        OpenApiDocumentTransformerContext context,
        CancellationToken cancellationToken)
    {
        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();

        document.Components.SecuritySchemes[SchemeName] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
            BearerFormat = "JWT",
            In = ParameterLocation.Header,
            Description =
                "Paste the token from auth/otp/verify. Swagger adds the 'Bearer ' prefix itself, "
                + "so enter the token on its own."
        };

        document.Security =
        [
            new OpenApiSecurityRequirement
            {
                [new OpenApiSecuritySchemeReference(SchemeName, document)] = []
            }
        ];

        return Task.CompletedTask;
    }
}
