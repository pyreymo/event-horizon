using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using EventHorizon.Localization;

namespace EventHorizon.Features;

internal static class FeatureUi
{
    public static bool Checkbox(string key, bool value, Action<bool> set)
    {
        if (!ImGui.Checkbox(Loc.Text("Config." + key), ref value))
            return false;
        set(value);
        return true;
    }

    public static bool Slider(string key, float value, float min, float max, Action<float> set, string format = "%.1f")
    {
        if (!ImGui.SliderFloat(Loc.Text("Config." + key), ref value, min, max, format))
            return false;
        set(value);
        return true;
    }

    public static bool Byte(string key, byte value, Action<byte> set)
    {
        var number = (int)value;
        if (!ImGui.SliderInt(Loc.Text("Config." + key), ref number, 0, 255))
            return false;
        set((byte)number);
        return true;
    }

    public static bool Color(string key, byte red, byte green, byte blue, byte alpha, Action<byte, byte, byte, byte> set)
    {
        var value = new Vector4(red / 255f, green / 255f, blue / 255f, alpha / 255f);
        if (!ImGui.ColorEdit4(Loc.Text("Config." + key), ref value))
            return false;
        set(ToByte(value.X), ToByte(value.Y), ToByte(value.Z), ToByte(value.W));
        return true;
    }

    private static byte ToByte(float value) => (byte)Math.Clamp((int)MathF.Round(value * 255), 0, 255);
}
