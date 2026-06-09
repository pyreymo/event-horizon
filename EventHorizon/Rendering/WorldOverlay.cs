using System;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Pictomancy;

namespace EventHorizon.Rendering;

internal sealed class WorldOverlay : IDisposable
{
    private const uint NearbyRangePreviewColor = 0x40000000;

    private readonly Configuration configuration;
    private readonly IObjectTable objectTable;
    private readonly PctContext? pctContext;

    public WorldOverlay(
        IDalamudPluginInterface pluginInterface,
        Configuration configuration,
        IObjectTable objectTable
    )
    {
        this.configuration = configuration;
        this.objectTable = objectTable;

        pctContext = PctService.Initialize(pluginInterface);
    }

    public void Draw()
    {
        if (
            pctContext == null
            || !configuration.HideAllOtherPlayers
            || !configuration.KeepNearbyPlayers
            || !configuration.PreviewNearbyPlayerRange
        )
        {
            return;
        }

        var player = objectTable.LocalPlayer;
        if (player == null)
        {
            return;
        }

        using var drawList = PctService.Draw(
            hints: new PctDrawHints
            {
                DefaultParams = new PctDxParams { OccludedAlpha = 0f, OcclusionTolerance = 0f },
            }
        );
        if (drawList == null)
        {
            return;
        }

        drawList.AddSphere(
            player.Position,
            configuration.KeepNearbyPlayersRange,
            NearbyRangePreviewColor
        );
    }

    public void Dispose()
    {
        pctContext?.Dispose();
    }
}
