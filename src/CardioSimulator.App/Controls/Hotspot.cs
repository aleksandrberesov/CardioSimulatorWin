using System;

namespace CardioSimulator.App.Controls;

public class Hotspot
{
    public string Id { get; set; } = string.Empty;
    public int Number { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public float[] Anchor { get; set; } = Array.Empty<float>();
    public float[] CameraPosition { get; set; } = Array.Empty<float>();
    public float[] CameraLookDirection { get; set; } = Array.Empty<float>();
    public float[] CameraUpDirection { get; set; } = Array.Empty<float>();
}
