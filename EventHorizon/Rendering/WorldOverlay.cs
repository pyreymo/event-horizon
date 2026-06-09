using System;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Pictomancy;

namespace EventHorizon.Rendering;

internal sealed class WorldOverlay(
    IDalamudPluginInterface pluginInterface,
    Configuration configuration,
    IObjectTable objectTable
) : IDisposable
{
    private const uint NearbyRangePreviewColor = 0x40000000;

    private readonly Configuration configuration = configuration;
    private readonly IObjectTable objectTable = objectTable;
    private readonly PctContext? pctContext = PctService.Initialize(pluginInterface);

    public void Draw()
    {
        if (
            !configuration.HideAllOtherPlayers
            || !configuration.KeepNearbyPlayers
            || !configuration.PreviewNearbyPlayerRange
        )
        {
            return;
        }

        DrawNearbyRangePreview();
    }

    public void Dispose()
    {
        pctContext?.Dispose();
    }

    private void DrawNearbyRangePreview()
    {
        if (pctContext == null)
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
}
