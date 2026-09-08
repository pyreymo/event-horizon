using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using EventHorizon.Settings;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace EventHorizon.Features;

internal sealed class FeatureConfigStore
{
    private readonly string directory;
    private readonly IPluginLog log;
    private readonly JObject legacy;
    private readonly HashSet<string> blockedWrites = [];
    private readonly JsonSerializer serializer = JsonSerializer.Create(
        new JsonSerializerSettings { ObjectCreationHandling = ObjectCreationHandling.Replace, TypeNameHandling = TypeNameHandling.None }
    );

    public FeatureConfigStore(IDalamudPluginInterface pluginInterface, IPluginLog log)
    {
        this.log = log;
        directory = Path.Combine(pluginInterface.GetPluginConfigDirectory(), "features");
        // Read before the plugin saves its current core configuration. Keep the source intact.
        legacy = ReadLegacy(pluginInterface.ConfigFile.FullName);
        var backup = ReadLegacy(pluginInterface.ConfigFile.FullName + ".bak");
        foreach (var property in backup.Properties())
            if (!legacy.ContainsKey(property.Name))
                legacy[property.Name] = property.Value.DeepClone();
    }

    private JObject ReadLegacy(string path)
    {
        try
        {
            return File.Exists(path) ? JObject.Parse(File.ReadAllText(path)) : new JObject();
        }
        catch (Exception exception)
        {
            log.Warning(exception, "Cannot read legacy feature settings: {Path}", path);
            return new JObject();
        }
    }

    public bool LegacyEnabled(string key, bool fallback) => legacy[key]?.Type == JTokenType.Boolean ? legacy[key]!.Value<bool>() : fallback;

    public T Load<T>(string id)
        where T : class, new()
    {
        var path = GetPath(id);
        try
        {
            var source = File.Exists(path) ? JObject.Parse(File.ReadAllText(path)) : (JObject)legacy.DeepClone();
            if (!File.Exists(path))
                source.Remove("Version");
            var result = new T();
            using var reader = source.CreateReader();
            serializer.Populate(reader, result);
            foreach (var property in typeof(T).GetProperties())
            {
                var range = property.GetCustomAttribute<ConfigRangeAttribute>();
                if (range != null && !range.IsValid(property.GetValue(result)))
                    property.SetValue(result, property.GetValue(new T()));
            }
            return result;
        }
        catch
        {
            // Do not replace an unreadable file with defaults merely by opening settings.
            blockedWrites.Add(id);
            throw;
        }
    }

    public void Save<T>(string id, T settings)
    {
        if (blockedWrites.Contains(id))
            throw new InvalidOperationException($"Configuration for '{id}' could not be read; original preserved.");
        Directory.CreateDirectory(directory);
        var path = GetPath(id);
        var temporary = path + ".tmp";
        try
        {
            // Preserve fields belonging to a newer version when downgrading.
            var json = File.Exists(path) ? JObject.Parse(File.ReadAllText(path)) : new JObject();
            json.Merge(
                JObject.FromObject(settings!, serializer),
                new JsonMergeSettings { MergeArrayHandling = MergeArrayHandling.Replace }
            );
            File.WriteAllText(temporary, json.ToString(Formatting.Indented));
            if (File.Exists(path))
                File.Replace(temporary, path, path + ".bak");
            else
                File.Move(temporary, path);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    private string GetPath(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || id.Contains(".."))
            throw new ArgumentException("Invalid feature id.", nameof(id));
        return Path.Combine(directory, id + ".json");
    }
}

internal sealed class FeaturePreferences
{
    public int Version { get; set; } = 1;
    public Dictionary<string, bool> Enabled { get; set; } = [];
}
