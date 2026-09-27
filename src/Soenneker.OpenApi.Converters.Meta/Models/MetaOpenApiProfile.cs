namespace Soenneker.OpenApi.Converters.Meta.Models;

/// <summary>Selects the operations and compatibility corrections included in a conversion.</summary>
public enum MetaOpenApiProfile
{
    /// <summary>Converts supplied specifications without publishing-specific filtering or corrections.</summary>
    Default,
    /// <summary>Facebook Page publishing, with corrected payloads, Kiota-compatible paths, and only referenced schemas.</summary>
    FacebookPublishing,
    /// <summary>Instagram publishing through Facebook Login, with container status and only referenced schemas.</summary>
    InstagramPublishing
}