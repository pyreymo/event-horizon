using System;
using Dalamud.Game.Gui.Dtr;
using Dalamud.Plugin.Services;
using EventHorizon.Culling;
using EventHorizon.Localization;
using EventHorizon.Settings;

namespace EventHorizon.Dtr;

internal sealed class DtrBar(
    IDtrBar dtrBar,
    Configuration configuration,
    Func<CullingStatus> getStatus,
    Action<bool> setEnabled,
    Action openSettings
) : IDisposable
{
    private IDtrBarEntry? entry;
    private long nextRefresh;

    public void Update()
    {
        if (Environment.TickCount64 >= nextRefresh)
            RefreshNow();
    }

    public void RefreshNow()
    {
        nextRefresh = Environment.TickCount64 + 500;
        if (!configuration.ShowDtrBar)
        {
            Dispose();
            return;
        }
        if (entry == null)
        {
            entry = dtrBar.Get("Event Horizon");
            entry.OnClick = interaction =>
            {
                if (interaction.ClickType == MouseClickType.Right)
                    openSettings();
                else if (interaction.ClickType == MouseClickType.Left)
                    setEnabled(!configuration.HideAllOtherPlayers);
            };
        }
        var status = getStatus();
        entry.Text = status.Mode == CullingRuntimeMode.Active ? $"EH · −{status.Rejected}" : $"EH · {Loc.Text($"State.{status.Mode}")}";
        entry.Tooltip = string.Format(
            Loc.Text("Dtr.Tooltip"),
            Loc.Text($"State.{status.Mode}"),
            Loc.Text("Dtr.Tooltip.LeftClick"),
            Loc.Text("Dtr.Tooltip.RightClick")
        );
        entry.Shown = true;
    }

    public void Dispose()
    {
        if (entry == null)
            return;
        entry.OnClick = null;
        entry.Remove();
        entry = null;
    }
}
