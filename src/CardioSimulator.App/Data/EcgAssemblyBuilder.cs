using System;
using System.Linq;
using CardioSimulator.Core.Data;
using CardioSimulator.Core.Domain;

namespace CardioSimulator.App.Data;

/// <summary>
/// Builds an <see cref="EcgAssembly"/> for the «Собери ЭКГ» question type by cutting a rhythm's lead into
/// N equal contiguous parts at <em>authoring</em> time. No wave detection — it works for any rhythm — so
/// this is thin glue over <see cref="EcgAssemblySlicer"/>: read the lead, cap the window, slice, store.
/// The sliced parts are stored on the question so the Testing runtime needs no signal processing.
/// </summary>
public static class EcgAssemblyBuilder
{
    private const int DefaultBaseline = 1024;

    /// <summary>The leading stretch of the lead we slice, capped so parts stay readable and rendering light.</summary>
    private const double MaxWindowSeconds = 5.0;

    /// <summary>
    /// Reads <paramref name="sourceId"/>'s <paramref name="lead"/> and cuts its leading window into
    /// <paramref name="partCount"/> equal baseline-zeroed parts. Returns null when the rhythm is missing
    /// or too short to yield the requested parts.
    /// </summary>
    public static EcgAssembly? Build(
        PathologyRepository repository, string sourceId, Lead lead, int partCount, float sampleRateHz)
    {
        if (repository is null || string.IsNullOrWhiteSpace(sourceId)) return null;
        partCount = Math.Clamp(partCount, EcgAssemblySlicer.MinParts, EcgAssemblySlicer.MaxParts);

        var file = repository.ReadPathology(sourceId);
        if (file is null) return null;
        var baseline = repository.Manifest()?.Baseline ?? DefaultBaseline;

        int[] samples;
        int sliceBaseline;
        if (file.Leads.TryGetValue(lead, out var stream))
        {
            samples = stream.Samples;
            sliceBaseline = baseline;
        }
        else
        {
            // Lead not physically stored (e.g. a derived lead): use the baseline-subtracted waveform.
            var wf = repository.LeadWaveform(sourceId, lead);
            if (wf is null) return null;
            samples = wf.Values.Select(v => (int)MathF.Round(v)).ToArray();
            sliceBaseline = 0;
        }

        var window = sampleRateHz > 0 ? (int)Math.Round(MaxWindowSeconds * sampleRateHz) : 0;
        var slices = EcgAssemblySlicer.Split(samples, sliceBaseline, partCount, window);
        if (slices is null || slices.Count < EcgAssemblySlicer.MinParts) return null;

        return new EcgAssembly(
            SampleRateHz: sampleRateHz > 0 ? (int)MathF.Round(sampleRateHz) : 0,
            Parts: slices.Select(s => new EcgAssemblyPart(s)).ToList(),
            SourcePathologyId: sourceId,
            SliceLead: lead);
    }
}
