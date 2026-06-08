using System;
using System.Collections.Generic;
using CardioSimulator.Core.Data;
using CardioSimulator.Core.Domain;

namespace CardioSimulator.App.Rendering;

/// <summary>
/// Builds the <c>(pathologyId, lead) → traces</c> resolver that <see cref="EcgSvgRenderer"/> /
/// <see cref="Controls.LectureWebView"/> use to turn <c>&lt;ecg&gt;</c> embeds into figures.
/// A null lead expands to all 12 leads.
/// </summary>
public static class EcgTraceResolver
{
    public static Func<string, Lead?, IReadOnlyList<EcgTrace>> ForRepository(PathologyRepository repository) =>
        (pathologyId, lead) =>
        {
            var leads = lead is { } single ? new[] { single } : Leads.All;
            var traces = new List<EcgTrace>();
            foreach (var l in leads)
            {
                var points = repository.LeadWaveform(pathologyId, l);
                if (points is not null) traces.Add(new EcgTrace(l, points));
            }
            return traces;
        };
}
