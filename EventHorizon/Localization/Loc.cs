using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Dalamud.Plugin;

namespace EventHorizon.Localization;

internal static class Loc
{
    private const string DefaultLanguage = "en";

    private static readonly Dictionary<string, string> Fallbacks = [];
    private static readonly Dictionary<string, string> Strings = [];

    public static void Load(IDalamudPluginInterface pluginInterface)
    {
        Fallbacks.Clear();
        Strings.Clear();

        LoadLanguage(pluginInterface, DefaultLanguage, Fallbacks);

        var language = NormalizeLanguage(pluginInterface.UiLanguage);
        if (language != DefaultLanguage)
        {
            LoadLanguage(pluginInterface, language, Strings);
        }
    }

    public static string Text(string key)
    {
        if (Strings.TryGetValue(key, out var localized))
        {
            return localized;
        }

        return Fallbacks.TryGetValue(key, out var fallback) ? fallback : key;
    }

    private static void LoadLanguage(
        IDalamudPluginInterface pluginInterface,
        string language,
        Dictionary<string, string> destination
    )
    {
        var directory = pluginInterface.AssemblyLocation.Directory?.FullName;
        if (directory is null)
        {
            return;
        }

        var path = Path.Combine(directory, "Localization", $"{language}.json");
        if (!File.Exists(path))
        {
            return;
        }

        var json = File.ReadAllText(path);
        var values = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
        if (values is null)
        {
            return;
        }

        foreach (var (key, value) in values)
        {
            destination[key] = value;
        }
    }

    private static string NormalizeLanguage(string language)
    {
        return language switch
        {
            "zh" or "zh-cn" or "zh-hans" => "zh",
            _ => DefaultLanguage,
        };
    }
}
