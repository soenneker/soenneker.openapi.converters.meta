using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Soenneker.OpenApi.Converters.Meta.Models;

namespace Soenneker.OpenApi.Converters.Meta.Abstract;

/// <summary>
/// Converts Meta Graph API JSON specifications into OpenAPI documents.
/// </summary>
public interface IMetaOpenApiConverter
{
    /// <summary>Converts Meta node specifications and enum arrays into OpenAPI 3.1.0 JSON. Keys are node names or JSON filenames.</summary>
    /// <remarks>
    /// GET edges are assumed to return paginated data. Mutation responses remain open objects because SDK return types
    /// do not specify their wire format. Use response overrides for known exceptions.
    /// Meta's SDKCodegen.json JSONPath patches are not applied automatically.
    /// </remarks>
    MetaOpenApiConversionResult Convert(IReadOnlyDictionary<string, string> specifications, MetaOpenApiConverterOptions options);

    /// <summary>Reads JSON files directly inside Meta's api_specs/specs directory and converts them. Makes no network requests.</summary>
    ValueTask<MetaOpenApiConversionResult> ConvertDirectoryAsync(string specificationsDirectory, MetaOpenApiConverterOptions options,
        CancellationToken cancellationToken = default);

    /// <summary>Converts a directory and writes UTF-8 OpenAPI JSON, replacing the destination if present. Output must be outside the input directory.</summary>
    ValueTask<MetaOpenApiConversionResult> ConvertToFileAsync(string specificationsDirectory, string outputPath,
        MetaOpenApiConverterOptions options, CancellationToken cancellationToken = default);
}
