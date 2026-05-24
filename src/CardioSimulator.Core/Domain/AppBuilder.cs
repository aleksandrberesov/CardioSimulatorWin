namespace CardioSimulator.Core.Domain;

/// <summary>Collects operating modes and builds the initial <see cref="AppStateModel"/>.</summary>
public sealed class AppBuilder
{
    private readonly List<OperatingModeModel> _modes = new();

    public AppBuilder AddMode(OperatingModeModel mode)
    {
        _modes.Add(mode);
        return this;
    }

    public AppStateModel Build(OperatingMode? initialMode = null)
    {
        if (_modes.Count == 0)
        {
            throw new InvalidOperationException(
                "At least one operating mode must be added before building.");
        }
        var initial = (initialMode is { } id
            ? _modes.FirstOrDefault(m => m.Id == id)
            : null) ?? _modes[0];
        return new AppStateModel(
            initialOperatingMode: initial,
            operatingModes: _modes.ToList());
    }
}
