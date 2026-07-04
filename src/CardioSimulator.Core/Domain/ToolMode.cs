namespace CardioSimulator.Core.Domain;

public enum ToolMode
{
    Select,
    Trace,
    Position,
    Points,
    Photo,
    Pan,
    /// <summary>Authoring annotation overlays ("tips"): the canvas places the selected
    /// <see cref="TipOverlayKind"/> on the trace instead of editing samples.</summary>
    Tips
}
