using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Soenneker.OpenApi.Converters.Meta.Abstract;

namespace Soenneker.OpenApi.Converters.Meta.Registrars;

/// <summary>
/// Converts Meta Graph API JSON specifications into OpenAPI documents.
/// </summary>
public static class MetaOpenApiConverterRegistrar
{
    /// <summary>
    /// Adds <see cref="IMetaOpenApiConverter"/> as a singleton service. <para/>
    /// </summary>
    public static IServiceCollection AddMetaOpenApiConverterAsSingleton(this IServiceCollection services)
    {
        services.TryAddSingleton<IMetaOpenApiConverter, MetaOpenApiConverter>();

        return services;
    }

    /// <summary>
    /// Adds <see cref="IMetaOpenApiConverter"/> as a scoped service. <para/>
    /// </summary>
    public static IServiceCollection AddMetaOpenApiConverterAsScoped(this IServiceCollection services)
    {
        services.TryAddScoped<IMetaOpenApiConverter, MetaOpenApiConverter>();

        return services;
    }
}
