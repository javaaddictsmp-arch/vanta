using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using TuneUp.Models;

namespace TuneUp.Services;

/// <summary>
/// Loads the JSON tweak catalog: the built-in one embedded in the EXE, plus any extra .json
/// files in %LocalAppData%\Vanta\Catalog. A user file with the same tweak id overrides the
/// built-in one. Adding a tweak never needs a code change.
/// </summary>
public sealed class TweakCatalogService
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public List<TweakDefinition> Tweaks { get; private set; } = new();

    public void Load()
    {
        var byId = new Dictionary<string, TweakDefinition>(StringComparer.OrdinalIgnoreCase);

        // 1) Built-in catalog (embedded resource "tweaks.json")
        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("tweaks.json");
            if (stream != null)
            {
                var catalog = JsonSerializer.Deserialize<TweakCatalog>(stream, Options);
                foreach (var t in catalog?.Tweaks ?? new()) Add(byId, t, "built-in");
            }
            else
            {
                AppLog.Error("Embedded tweaks.json not found");
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("Failed to read built-in tweak catalog", ex);
        }

        // 2) User catalogs
        try
        {
            foreach (var file in Directory.EnumerateFiles(AppPaths.Catalog, "*.json"))
            {
                try
                {
                    var catalog = JsonSerializer.Deserialize<TweakCatalog>(File.ReadAllText(file), Options);
                    foreach (var t in catalog?.Tweaks ?? new()) Add(byId, t, Path.GetFileName(file));
                }
                catch (Exception ex)
                {
                    AppLog.Error($"Skipping invalid catalog file {file}", ex);
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("Could not scan user catalog folder", ex);
        }

        Tweaks = byId.Values.OrderBy(t => t.Category).ThenBy(t => t.Name).ToList();
        AppLog.Info($"Tweak catalog loaded: {Tweaks.Count} tweaks");
    }

    private static void Add(Dictionary<string, TweakDefinition> byId, TweakDefinition t, string source)
    {
        if (string.IsNullOrWhiteSpace(t.Id) || t.Operations.Count == 0)
        {
            AppLog.Warn($"Ignoring tweak without id/operations in {source}: '{t.Name}'");
            return;
        }
        byId[t.Id] = t;
    }
}
