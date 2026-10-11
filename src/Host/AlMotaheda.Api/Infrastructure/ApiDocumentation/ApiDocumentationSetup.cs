using Microsoft.OpenApi.Models;

namespace AlMotaheda.Api.Infrastructure.ApiDocumentation;

/// <summary>
/// OpenAPI (Swagger) document at <c>/swagger/v1/swagger.json</c> and Swagger UI at <c>/swagger</c>.
/// </summary>
/// <remarks>
/// Enabled automatically in Development. In other environments, set <c>ApiDocumentation:Enabled</c> to <c>true</c>.
/// </remarks>
internal static class ApiDocumentationSetup
{
    private const string EnabledSettingKey = "ApiDocumentation:Enabled";
    private const string DocumentName = "v1";
    private const string ApiTitle = "AlMotaheda ERP API";

    public static IServiceCollection AddApiDocumentation(this IServiceCollection services)
    {
        services.AddEndpointsApiExplorer();
        services.AddSwaggerGen(options =>
        {
            options.SwaggerDoc(DocumentName, new OpenApiInfo
            {
                Title = ApiTitle,
                Version = DocumentName,
            });

            // Vertical slices nest their models (CreateProject.Request, UploadDocument.Request, ...).
            // Default schema ids would all be "Request" and collide, so include the declaring type.
            options.CustomSchemaIds(CreateSchemaId);
        });

        return services;
    }

    public static bool IsApiDocumentationEnabled(this WebApplication app) =>
        app.Environment.IsDevelopment() || app.Configuration.GetValue<bool>(EnabledSettingKey);

    public static WebApplication UseApiDocumentation(this WebApplication app)
    {
        app.UseSwagger();
        app.UseSwaggerUI(options =>
        {
            options.SwaggerEndpoint($"/swagger/{DocumentName}/swagger.json", $"{ApiTitle} {DocumentName}");
            options.DocumentTitle = ApiTitle;
        });

        return app;
    }

    /// <summary>
    /// Builds readable, unique schema ids: <c>CreateProject.Request</c> becomes <c>CreateProjectRequest</c>,
    /// <c>PagedResult&lt;Item&gt;</c> becomes <c>PagedResultOfItem</c>.
    /// </summary>
    internal static string CreateSchemaId(Type type)
    {
        var name = type.Name;

        if (type.IsGenericType)
        {
            var baseName = name[..name.IndexOf('`')];
            var arguments = string.Join("And", type.GetGenericArguments().Select(CreateSchemaId));
            name = $"{baseName}Of{arguments}";
        }

        return type.IsNested && type.DeclaringType is not null && !type.IsGenericParameter
            ? CreateSchemaId(type.DeclaringType) + name
            : name;
    }
}
