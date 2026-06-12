namespace CardioSimulator.Core.Domain;

public record PhotoTransform(
    float OffsetX,
    float OffsetY,
    float Scale,
    float RotationDeg,
    float Alpha,
    bool IsLocked,
    bool IsVisible = true)
{
    public static readonly PhotoTransform Default = new(0f, 0f, 1f, 0f, 0.5f, false, true);
}
