using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Keys;
using EventHorizon.Localization;

namespace EventHorizon.UI.Config;

internal partial class ConfigWindow
{
    private void DrawTemporaryShowAllPlayersKey()
    {
        var enabled = configuration.EnableTemporaryShowAllPlayersShortcut;
        var enabledChanged = ImGui.Checkbox("##EnableTemporaryShowAllPlayersShortcut", ref enabled);
        ImGui.SameLine();
        ImGui.TextUnformatted(Loc.Text("Config.TemporarilyShowAllPlayersKey"));
        if (ImGui.IsItemClicked())
        {
            enabled = !enabled;
            enabledChanged = true;
        }

        if (enabledChanged)
        {
            configuration.EnableTemporaryShowAllPlayersShortcut = enabled;
            if (!enabled)
            {
                capturingTemporaryShowAllPlayersShortcut = false;
                capturedTemporaryShowAllPlayersKeys.Clear();
            }

            configuration.Save();
        }

        ImGui.SameLine();
        if (!enabled)
        {
            ImGui.BeginDisabled();
        }

        var clearButtonWidth = ImGui.CalcTextSize(Loc.Text("Config.Shortcut.Clear")).X + ImGui.GetStyle().FramePadding.X * 2f;
        ImGui.SetNextItemWidth(-clearButtonWidth - ImGui.GetStyle().ItemSpacing.X);
        var shortcutLabel = capturingTemporaryShowAllPlayersShortcut
            ? BuildShortcutLabel(capturedTemporaryShowAllPlayersKeys, Loc.Text("Config.Shortcut.Recording"))
            : BuildShortcutLabel(configuration.TemporarilyShowAllPlayersKeys, Loc.Text("Config.Shortcut.Unbound"));

        if (ImGui.Button($"{shortcutLabel}##TemporarilyShowAllPlayersKey", new Vector2(ImGui.CalcItemWidth(), 0f)))
        {
            capturingTemporaryShowAllPlayersShortcut = true;
            capturedTemporaryShowAllPlayersKeys.Clear();
        }

        ImGui.SameLine();
        if (ImGui.Button($"{Loc.Text("Config.Shortcut.Clear")}##ClearTemporarilyShowAllPlayersKey"))
        {
            capturingTemporaryShowAllPlayersShortcut = false;
            capturedTemporaryShowAllPlayersKeys.Clear();
            configuration.TemporarilyShowAllPlayersKeys.Clear();
            configuration.Save();
        }

        if (!enabled)
        {
            ImGui.EndDisabled();
        }

        DrawHelpMarker(Loc.Text("Config.TemporarilyShowAllPlayersKey.Help"));

        if (capturingTemporaryShowAllPlayersShortcut)
        {
            CaptureTemporaryShowAllPlayersShortcut();
        }
    }

    private void CaptureTemporaryShowAllPlayersShortcut()
    {
        if (Plugin.KeyState[VirtualKey.ESCAPE])
        {
            capturingTemporaryShowAllPlayersShortcut = false;
            capturedTemporaryShowAllPlayersKeys.Clear();
            return;
        }

        var anyKeyHeld = false;
        foreach (var key in Plugin.KeyState.GetValidVirtualKeys())
        {
            var keyCode = (int)key;
            if (IsIgnoredCaptureKey(key) || !Plugin.KeyState[key])
            {
                continue;
            }

            anyKeyHeld = true;
            capturedTemporaryShowAllPlayersKeys.Add(keyCode);
        }

        if (capturedTemporaryShowAllPlayersKeys.Count == 0 || anyKeyHeld)
        {
            return;
        }

        var keys = new List<int>(capturedTemporaryShowAllPlayersKeys);
        keys.Sort();
        configuration.TemporarilyShowAllPlayersKeys = keys;
        configuration.Save();
        capturingTemporaryShowAllPlayersShortcut = false;
        capturedTemporaryShowAllPlayersKeys.Clear();
    }

    private static bool IsIgnoredCaptureKey(VirtualKey key) =>
        key
            is VirtualKey.LBUTTON
                or VirtualKey.RBUTTON
                or VirtualKey.MBUTTON
                or VirtualKey.XBUTTON1
                or VirtualKey.XBUTTON2
                or VirtualKey.ESCAPE
                or VirtualKey.LSHIFT
                or VirtualKey.RSHIFT
                or VirtualKey.LCONTROL
                or VirtualKey.RCONTROL
                or VirtualKey.LMENU
                or VirtualKey.RMENU;

    private static string BuildShortcutLabel(IEnumerable<int> keys, string emptyLabel)
    {
        var labels = new List<string>();
        var addedKeys = new HashSet<int>();
        foreach (var key in keys)
        {
            if (addedKeys.Add(key))
            {
                labels.Add(GetKeyLabel((VirtualKey)key));
            }
        }

        return labels.Count == 0 ? emptyLabel : string.Join(" + ", labels);
    }

    private static string GetKeyLabel(VirtualKey key) =>
        key switch
        {
            VirtualKey.MENU => "Alt",
            VirtualKey.CONTROL => "Ctrl",
            VirtualKey.SHIFT => "Shift",
            >= VirtualKey.KEY_0 and <= VirtualKey.KEY_9 => ((char)key).ToString(),
            >= VirtualKey.A and <= VirtualKey.Z => ((char)key).ToString(),
            _ => key.ToString(),
        };
}
