using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace EventHorizon.WorldGraphics;

internal enum WorldDotScope
{
    HiddenPlayer,
    TargetingMe,
}

internal readonly record struct WorldDot(Vector3 Position, uint Color, float Radius)
{
    public static uint PackColor(byte red, byte green, byte blue, byte alpha)
    {
        return red | ((uint)green << 8) | ((uint)blue << 16) | ((uint)alpha << 24);
    }
}

internal interface IWorldDotOverlay
{
    void Replace(WorldDotScope scope, ReadOnlySpan<WorldDot> dots);
    void Clear(WorldDotScope scope);
}

internal sealed class WorldDotOverlay : IWorldDotOverlay, IDisposable
{
    private readonly IGameGui gameGui;
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly ScopeBuffer[] buffers = [new(), new()];
    private bool disposed;

    public WorldDotOverlay(IGameGui gameGui, IDalamudPluginInterface pluginInterface)
    {
        this.gameGui = gameGui;
        this.pluginInterface = pluginInterface;
        pluginInterface.UiBuilder.Draw += Draw;
    }

    public void Replace(WorldDotScope scope, ReadOnlySpan<WorldDot> dots)
    {
        if (!disposed)
        {
            buffers[(int)scope].Replace(dots);
        }
    }

    public void Clear(WorldDotScope scope)
    {
        buffers[(int)scope].Count = 0;
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        pluginInterface.UiBuilder.Draw -= Draw;
        foreach (var buffer in buffers)
        {
            buffer.Count = 0;
        }
    }

    private void Draw()
    {
        var drawList = ImGui.GetBackgroundDrawList();
        foreach (var buffer in buffers)
        {
            for (var index = 0; index < buffer.Count; index++)
            {
                var dot = buffer.Items[index];
                if (gameGui.WorldToScreen(dot.Position, out var screenPosition, out var inView) && inView)
                {
                    drawList.AddCircleFilled(screenPosition, dot.Radius, dot.Color);
                }
            }
        }
    }

    private sealed class ScopeBuffer
    {
        public WorldDot[] Items = new WorldDot[64];
        public int Count { get; set; }

        public void Replace(ReadOnlySpan<WorldDot> dots)
        {
            if (Items.Length < dots.Length)
            {
                Array.Resize(ref Items, Math.Max(dots.Length, Items.Length * 2));
            }

            dots.CopyTo(Items);
            Count = dots.Length;
        }
    }
}
