using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using EventHorizon.Localization;

namespace EventHorizon.Features;

internal sealed class FeatureHost : IDisposable
{
    private readonly List<FeatureRuntime> features;
    private readonly FeaturePreferences preferences;
    private readonly FeatureConfigStore store;
    private readonly IFramework framework;
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly IPluginLog log;
    private readonly Dictionary<string, (FeatureScope Scope, Action Action)> commands = new(StringComparer.OrdinalIgnoreCase);
    private bool disposed;
    private string? selectedFeatureId;

    public FeatureHost(
        IEnumerable<IFeatureDefinition> definitions,
        FeatureConfigStore store,
        IFramework framework,
        IDalamudPluginInterface pluginInterface,
        IPluginLog log,
        bool safeMode
    )
    {
        this.store = store;
        this.framework = framework;
        this.pluginInterface = pluginInterface;
        this.log = log;
        features = [];
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var definition in definitions)
        {
            var feature = new FeatureRuntime(definition, definition.GetDefaultEnabled(store));
            if (!ids.Add(feature.Id))
                throw new InvalidOperationException($"Duplicate feature id: {feature.Id}");
            features.Add(feature);
        }
        try
        {
            preferences = store.Load<FeaturePreferences>("runtime");
            preferences.Enabled ??= [];
        }
        catch (Exception exception)
        {
            log.Error(exception, "Feature preferences are unreadable; starting with all features disabled.");
            preferences = new FeaturePreferences();
            foreach (var feature in features)
                preferences.Enabled[feature.Id] = false;
        }
        foreach (var feature in features)
        {
            preferences.Enabled.TryAdd(feature.Id, feature.DefaultEnabled);
            if (safeMode)
                preferences.Enabled[feature.Id] = false;
            feature.PendingEnabled = preferences.Enabled[feature.Id];
        }
        try
        {
            store.Save("runtime", preferences);
        }
        catch (Exception exception)
        {
            log.Error(exception, "Could not save feature preferences; original preserved.");
        }
        framework.Update += OnUpdate;
    }

    public void RegisterCommand(FeatureScope scope, string command, Action callback)
    {
        if (commands.ContainsKey(command))
            throw new InvalidOperationException($"Duplicate feature command: {command}");
        scope.Defer(() => commands.Remove(command));
        commands.Add(command, (scope, callback));
    }

    public bool TryCommand(string command)
    {
        if (!commands.TryGetValue(command, out var entry))
            return false;
        entry.Scope.Run(entry.Action);
        return true;
    }

    private void OnUpdate(IFramework _)
    {
        if (disposed)
            return;
        foreach (var feature in features)
        {
            if (feature.State == FeatureState.Faulted && feature.Scope != null)
                Stop(feature, true);
            if (feature.PendingEnabled is not { } enabled)
                continue;
            feature.PendingEnabled = null;
            if (enabled)
                Start(feature);
            else
                Stop(feature, feature.State == FeatureState.Faulted);
        }
    }

    private void Start(FeatureRuntime feature)
    {
        if (feature.State is FeatureState.Enabled or FeatureState.Faulted or FeatureState.Disposed)
            return;
        feature.State = FeatureState.Enabling;
        try
        {
            feature.Instance ??= feature.Definition.Create(store);
            feature.Scope = new FeatureScope(framework, pluginInterface, exception => Fail(feature, exception));
            feature.Instance.Enable(feature.Scope);
            if (feature.State == FeatureState.Faulted)
                Stop(feature, true);
            else
                feature.State = FeatureState.Enabled;
        }
        catch (Exception exception)
        {
            Fail(feature, exception);
            Stop(feature, true);
        }
    }

    private void Fail(FeatureRuntime feature, Exception exception)
    {
        feature.Scope?.Deactivate();
        feature.Error = exception;
        feature.State = FeatureState.Faulted;
        log.Error(exception, "Feature {Feature} failed.", feature.Id);
    }

    private void Stop(FeatureRuntime feature, bool faulted)
    {
        if (feature.State == FeatureState.Disposed)
            return;
        feature.Scope?.Deactivate();
        feature.State = FeatureState.Disabling;
        if (feature.Scope != null)
        {
            try
            {
                feature.Instance?.Disable();
            }
            catch (Exception exception)
            {
                faulted = true;
                Fail(feature, exception);
            }
            try
            {
                feature.Scope.Dispose();
            }
            catch (Exception exception)
            {
                faulted = true;
                Fail(feature, exception);
            }
            feature.Scope = null;
        }
        feature.State = faulted ? FeatureState.Faulted : FeatureState.Disabled;
    }

    public void DrawSettings()
    {
        if (features.Count == 0)
            return;
        var selected = features.Find(feature => feature.Id == selectedFeatureId) ?? features[0];
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var fontSize = ImGui.GetFontSize();
        var available = ImGui.GetContentRegionAvail().X;
        var listWidth = Math.Min(fontSize * 11f, available * 0.35f);
        var listVisible = ImGui.BeginChild("FeatureList", new Vector2(listWidth, 0), true);
        try
        {
            if (listVisible)
            {
                ImGui.TextDisabled(Loc.Text("Feature.Modules"));
                ImGui.Spacing();
                foreach (var feature in features)
                {
                    ImGui.PushID(feature.Id);
                    try
                    {
                        var wanted = preferences.Enabled.GetValueOrDefault(feature.Id, feature.DefaultEnabled);
                        if (ImGui.Checkbox("##Enabled", ref wanted))
                        {
                            SetEnabled(feature, wanted);
                            selected = feature;
                        }
                        if (ImGui.IsItemHovered())
                            ImGui.SetTooltip(Loc.Text("Feature.Toggle"));
                        ImGui.SameLine();
                        if (
                            ImGui.Selectable(
                                feature.Title + "###Select",
                                feature == selected,
                                ImGuiSelectableFlags.None,
                                new Vector2(0, ImGui.GetFrameHeight())
                            )
                        )
                        {
                            selectedFeatureId = feature.Id;
                            selected = feature;
                        }
                    }
                    finally
                    {
                        ImGui.PopID();
                    }
                }
            }
        }
        finally
        {
            ImGui.EndChild();
        }

        ImGui.SameLine(0, spacing);
        var settingsVisible = ImGui.BeginChild("FeatureDetails", Vector2.Zero, true);
        try
        {
            if (!settingsVisible)
                return;
            ImGui.PushID(selected.Id);
            try
            {
                ImGui.TextUnformatted(selected.Title);
                ImGui.Spacing();
                ImGui.TextWrapped(selected.Description);
                ImGui.Spacing();
                ImGui.Separator();
                ImGui.Spacing();
                if (selected.Error != null)
                {
                    ImGui.TextWrapped(Loc.Text("Feature.Faulted"));
                    ImGui.TextWrapped(selected.Error.Message);
                    return;
                }
                if (!preferences.Enabled.GetValueOrDefault(selected.Id, selected.DefaultEnabled))
                {
                    ImGui.TextWrapped(Loc.Text("Feature.DisabledHint"));
                    ImGui.Spacing();
                }
                ImGui.PushItemWidth(Math.Min(fontSize * 12f, ImGui.GetContentRegionAvail().X * 0.5f));
                try
                {
                    selected.Instance ??= selected.Definition.Create(store);
                    selected.Instance.DrawSettings();
                }
                catch (Exception exception)
                {
                    Fail(selected, exception);
                }
                finally
                {
                    ImGui.PopItemWidth();
                }
            }
            finally
            {
                ImGui.PopID();
            }
        }
        finally
        {
            ImGui.EndChild();
        }
    }

    private void SetEnabled(FeatureRuntime feature, bool enabled)
    {
        preferences.Enabled[feature.Id] = enabled;
        feature.PendingEnabled = enabled;
        selectedFeatureId = feature.Id;
        try
        {
            store.Save("runtime", preferences);
        }
        catch (Exception exception)
        {
            log.Error(exception, "Failed to save feature preferences.");
        }
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        framework.Update -= OnUpdate;
        for (var i = features.Count - 1; i >= 0; i--)
        {
            var feature = features[i];
            Stop(feature, feature.State == FeatureState.Faulted);
            try
            {
                feature.Instance?.Dispose();
            }
            catch (Exception exception)
            {
                log.Error(exception, "Failed to dispose feature {Feature}.", feature.Id);
            }
            feature.Instance = null;
            feature.State = FeatureState.Disposed;
        }
        commands.Clear();
    }

    private enum FeatureState
    {
        Disabled,
        Enabling,
        Enabled,
        Disabling,
        Faulted,
        Disposed,
    }

    private sealed class FeatureRuntime(IFeatureDefinition definition, bool defaultEnabled)
    {
        public IFeatureDefinition Definition { get; } = definition;
        public string Id => Definition.Id;
        public string Title => Loc.Text(Definition.TitleKey);
        public string Description => Loc.Text(Definition.TitleKey + ".Description");
        public bool DefaultEnabled { get; } = defaultEnabled;
        public IFeature? Instance { get; set; }
        public FeatureScope? Scope { get; set; }
        public FeatureState State { get; set; }
        public Exception? Error { get; set; }
        public bool? PendingEnabled { get; set; }
    }
}
