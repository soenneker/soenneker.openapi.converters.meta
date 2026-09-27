using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Soenneker.OpenApi.Converters.Meta.Abstract;
using Soenneker.OpenApi.Converters.Meta.Internal;
using Soenneker.OpenApi.Converters.Meta.Models;

namespace Soenneker.OpenApi.Converters.Meta;

public sealed class MetaOpenApiConverter : IMetaOpenApiConverter
{
    public MetaOpenApiConversionResult Convert(IReadOnlyDictionary<string, string> specifications, MetaOpenApiConverterOptions options)
        => ConvertCore(specifications, options, CancellationToken.None);

    public async ValueTask<MetaOpenApiConversionResult> ConvertDirectoryAsync(string specificationsDirectory, MetaOpenApiConverterOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(specificationsDirectory);
        var specifications = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (string path in Directory.EnumerateFiles(specificationsDirectory, "*.json").Order(StringComparer.Ordinal))
            specifications.Add(Path.GetFileNameWithoutExtension(path), await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false));
        return ConvertCore(specifications, options, cancellationToken);
    }

    public async ValueTask<MetaOpenApiConversionResult> ConvertToFileAsync(string specificationsDirectory, string outputPath,
        MetaOpenApiConverterOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(specificationsDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        string input = Path.GetFullPath(specificationsDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string output = Path.GetFullPath(outputPath);
        if (output.StartsWith(input, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new ArgumentException("Output must be outside the specifications directory.", nameof(outputPath));

        MetaOpenApiConversionResult result = await ConvertDirectoryAsync(specificationsDirectory, options, cancellationToken).ConfigureAwait(false);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        await File.WriteAllTextAsync(output, result.ToJson(), new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        return result;
    }

    private static MetaOpenApiConversionResult ConvertCore(IReadOnlyDictionary<string, string> specifications,
        MetaOpenApiConverterOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(specifications);
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(options.GraphApiVersion) || !Regex.IsMatch(options.GraphApiVersion, @"^v\d+\.\d+$"))
            throw new ArgumentException("GraphApiVersion must use the form v25.0.", nameof(options));
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Title);
        if (!Uri.TryCreate(options.ServerUrl, UriKind.Absolute, out Uri? server) || server.Scheme != "https" ||
            server.AbsolutePath != "/" || server.Query.Length != 0 || server.Fragment.Length != 0 || server.UserInfo.Length != 0)
            throw new ArgumentException("ServerUrl must be an HTTPS origin without a path, credentials, query, or fragment.", nameof(options));
        if (specifications.Count == 0) throw new ArgumentException("At least one specification is required.", nameof(specifications));
        if (!Enum.IsDefined(options.Profile)) throw new ArgumentOutOfRangeException(nameof(options), "Unknown conversion profile.");
        return options.Profile == MetaOpenApiProfile.Default
            ? new Conversion(options, cancellationToken).Run(specifications)
            : PublishingConversion.Run(specifications, options, cancellationToken);
    }
}
