using System;

namespace EventHorizon.Culling.Visibility;

internal sealed class ShowTransitionBudget
{
    private const double ShowTransitionsPerSecond = 6.0;
    private const double ShowTransitionCapacity = 1.0;
    private const int MaxShowStartsPerFrame = 1;

    private double tokens = ShowTransitionCapacity;
    private long lastRefill = Environment.TickCount64;
    private int showStartsThisFrame;

    public double CurrentTokens => tokens;

    public void BeginFrame()
    {
        var now = Environment.TickCount64;
        var elapsedMs = Math.Max(0, now - lastRefill);
        tokens = Math.Min(ShowTransitionCapacity, tokens + (elapsedMs / 1000.0 * ShowTransitionsPerSecond));
        lastRefill = now;
        showStartsThisFrame = 0;
    }

    public bool CanStartShow()
    {
        return showStartsThisFrame < MaxShowStartsPerFrame && tokens >= 1.0;
    }

    public void ConsumeShow()
    {
        tokens = Math.Max(0.0, tokens - 1.0);
        showStartsThisFrame++;
    }

    public void Reset()
    {
        tokens = ShowTransitionCapacity;
        lastRefill = Environment.TickCount64;
        showStartsThisFrame = 0;
    }
}
