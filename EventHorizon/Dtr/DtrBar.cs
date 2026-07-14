using System;
using System.Collections.Generic;
using Dalamud.Game.Gui.Dtr;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Plugin.Services;
using EventHorizon.Culling;
using EventHorizon.Localization;
using EventHorizon.Settings;

namespace EventHorizon.Dtr;

internal sealed class DtrBar : IDisposable
{
    private const int RefreshIntervalMs = 1_000;
    private readonly Configuration configuration;
    private readonly Func<CullingStatus> getCullingStatus;
    private readonly Action<bool> setPlayerHidingEnabled;
    private readonly Action openSettings;
    private readonly IDtrBar dtrBar;
    private IDtrBarEntry? entry;
    private long nextRefresh;

    public DtrBar(
        IDtrBar dtrBar,
        Configuration configuration,
        Func<CullingStatus> getCullingStatus,
        Action<bool> setPlayerHidingEnabled,
        Action openSettings
    )
    {
        this.configuration = configuration;
        this.getCullingStatus = getCullingStatus;
        this.setPlayerHidingEnabled = setPlayerHidingEnabled;
        this.openSettings = openSettings;
        this.dtrBar = dtrBar;

        RefreshNow();
    }

    public void Update()
    {
        var now = Environment.TickCount64;
        if (now < nextRefresh)
        {
            return;
        }

        RefreshNow();
    }

    public void RefreshNow()
    {
        nextRefresh = Environment.TickCount64 + RefreshIntervalMs;
        if (!configuration.ShowDtrBar)
        {
            RemoveEntry();
            return;
        }

        var entry = EnsureEntry();
        var state = GetState();
        entry.Text = GetEntryText(state);
        entry.Tooltip = BuildTooltip(state);
        entry.Shown = true;
    }

    public void Dispose()
    {
        RemoveEntry();
    }

    private void OnClick(DtrInteractionEvent interaction)
    {
        if (interaction.ClickType == MouseClickType.Right)
        {
            openSettings();
            return;
        }

        if (interaction.ClickType != MouseClickType.Left)
        {
            return;
        }

        setPlayerHidingEnabled(!configuration.HideAllOtherPlayers);
    }

    private IDtrBarEntry EnsureEntry()
    {
        if (entry != null)
        {
            return entry;
        }

        entry = dtrBar.Get(Loc.Text("Config.Title"));
        entry.OnClick = OnClick;

        return entry;
    }

    private void RemoveEntry()
    {
        if (entry == null)
        {
            return;
        }

        entry.OnClick = null;
        entry.Remove();
        entry = null;
    }

    private static string GetTextKey(DtrBarState state)
    {
        if (!state.Enabled)
        {
            return "Dtr.Text.Disabled";
        }

        return state.PauseReasonKeys.Count > 0 ? "Dtr.Text.Paused" : "Dtr.Text.Enabled";
    }

    private DtrBarState GetState()
    {
        var status = getCullingStatus();
        if (!status.Enabled)
        {
            return new(false, []);
        }

        var pauseReasonKeys = new List<string>();
        if (status.SuspendedByTemporaryReveal)
        {
            pauseReasonKeys.Add("PauseReason.TemporaryReveal");
        }

        if (status.SuspendedInDuty)
        {
            pauseReasonKeys.Add("PauseReason.InDuty");
        }

        if (status.SuspendedByLowPlayerCount)
        {
            pauseReasonKeys.Add("PauseReason.LowPlayerCount");
        }

        return new(true, pauseReasonKeys);
    }

    private unsafe string GetEntryText(DtrBarState state)
    {
        var text = Loc.Text(GetTextKey(state));
        if (!configuration.ShowFrameRateInDtrBar)
        {
            return text;
        }

        var framework = FFXIVClientStructs.FFXIV.Client.System.Framework.Framework.Instance();
        var frameRate = framework != null ? framework->FrameRate : 0f;
        return string.Format(Loc.Text("Dtr.Text.WithFps"), text, frameRate);
    }

    private static SeString BuildTooltip(DtrBarState state)
    {
        return string.Format(
            Loc.Text("Dtr.Tooltip"),
            GetStatusText(state),
            Loc.Text("Dtr.Tooltip.LeftClick"),
            Loc.Text("Dtr.Tooltip.RightClick")
        );
    }

    private static string GetStatusText(DtrBarState state)
    {
        if (!state.Enabled)
        {
            return Loc.Text("Status.Disabled");
        }

        if (state.PauseReasonKeys.Count == 0)
        {
            return Loc.Text("Status.Enabled");
        }

        var reasons = new List<string>();
        foreach (var key in state.PauseReasonKeys)
        {
            reasons.Add(Loc.Text(key));
        }

        return string.Format(Loc.Text("Status.Paused"), string.Join(Loc.Text("PauseReason.Separator"), reasons));
    }

    private sealed record DtrBarState(bool Enabled, IReadOnlyList<string> PauseReasonKeys);
}
