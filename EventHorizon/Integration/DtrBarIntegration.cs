using System;
using System.Collections.Generic;
using Dalamud.Game.Gui.Dtr;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Plugin.Services;
using EventHorizon.Localization;

namespace EventHorizon.Integration;

internal sealed class DtrBarIntegration : IDisposable
{
    private readonly Configuration configuration;
    private readonly Func<DtrBarState> getState;
    private readonly Action openSettings;
    private readonly Action refreshObjectCulling;
    private readonly IDtrBarEntry entry;

    public DtrBarIntegration(
        IDtrBar dtrBar,
        Configuration configuration,
        Func<DtrBarState> getState,
        Action openSettings,
        Action refreshObjectCulling
    )
    {
        this.configuration = configuration;
        this.getState = getState;
        this.openSettings = openSettings;
        this.refreshObjectCulling = refreshObjectCulling;

        entry = dtrBar.Get(Loc.Text("Config.Title"));
        entry.MinimumWidth = 70;
        entry.OnClick = OnClick;

        Refresh();
    }

    public void Refresh()
    {
        var state = getState();
        entry.Text = Loc.Text(GetTextKey(state));
        entry.Tooltip = BuildTooltip(state);
        entry.Shown = true;
    }

    public void Dispose()
    {
        entry.OnClick = null;
        entry.Remove();
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

        configuration.HideAllOtherPlayers = !configuration.HideAllOtherPlayers;
        configuration.Save();
        Refresh();
        refreshObjectCulling();
    }

    private static string GetTextKey(DtrBarState state)
    {
        if (!state.Enabled)
        {
            return "Dtr.Text.Disabled";
        }

        return state.PauseReasonKeys.Count > 0 ? "Dtr.Text.Paused" : "Dtr.Text.Enabled";
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
            return Loc.Text("Dtr.Status.Disabled");
        }

        if (state.PauseReasonKeys.Count == 0)
        {
            return Loc.Text("Dtr.Status.Enabled");
        }

        var reasons = new List<string>();
        foreach (var key in state.PauseReasonKeys)
        {
            reasons.Add(Loc.Text(key));
        }

        return string.Format(
            Loc.Text("Dtr.Status.Paused"),
            string.Join(Loc.Text("Dtr.PauseReason.Separator"), reasons)
        );
    }
}

internal sealed record DtrBarState(bool Enabled, IReadOnlyList<string> PauseReasonKeys);
