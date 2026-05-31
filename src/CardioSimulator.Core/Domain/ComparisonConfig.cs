using System.Collections.Generic;

namespace CardioSimulator.Core.Domain;

public record ComparisonConfig(
    Lead TargetLead,
    IReadOnlyList<string> SelectedPathologyIds);
