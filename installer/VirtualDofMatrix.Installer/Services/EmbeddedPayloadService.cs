using System.IO;
using System.Reflection;

namespace VirtualDofMatrix.Installer.Services;

internal static class EmbeddedPayloadService
{
    private const string AppPayloadPrefix = "payload/app/";
    private const string DofPayloadPrefix = "payload/DOF/";
    private const string ReleasePayloadPrefix = "payload/release/";

    private static readonly Lazy<string> ExtractionRoot = new(ExtractPayloads);

    public static string AppPayloadDirectory => Path.Combine(ExtractionRoot.Value, "app");

    public static string DofPayloadDirectory => Path.Combine(ExtractionRoot.Value, "DOF");

    public static string ReleasePayloadDirectory => Path.Combine(ExtractionRoot.Value, "release");

    private static string ExtractPayloads()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceNames = assembly.GetManifestResourceNames()
            .Where(IsPayloadResource)
            .ToArray();

        if (resourceNames.Length == 0)
            throw new DirectoryNotFoundException("Installer payload resources were not embedded in this executable.");

        // Use a stable temp location and overwrite files so rerunning the same installer refreshes
        // the extracted payload before the real install copy begins.
        var root = Path.Combine(
            Path.GetTempPath(),
            "VirtualDofMatrix.Installer",
            assembly.GetName().Version?.ToString() ?? "current",
            "payload");

        foreach (var resourceName in resourceNames)
        {
            var relativePath = resourceName.Replace('/', Path.DirectorySeparatorChar);
            var destination = Path.Combine(root, relativePath["payload/".Length..]);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

            using var source = assembly.GetManifestResourceStream(resourceName)
                ?? throw new FileNotFoundException($"Embedded payload resource could not be opened: {resourceName}");
            using var target = File.Create(destination);
            source.CopyTo(target);
        }

        return root;
    }

    private static bool IsPayloadResource(string resourceName) =>
        resourceName.StartsWith(AppPayloadPrefix, StringComparison.Ordinal) ||
        resourceName.StartsWith(DofPayloadPrefix, StringComparison.Ordinal) ||
        resourceName.StartsWith(ReleasePayloadPrefix, StringComparison.Ordinal);
}
