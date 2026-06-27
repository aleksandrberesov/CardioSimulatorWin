using CardioSimulator.Core.Domain;

namespace CardioSimulator.Core.Data.Wfdb;

/// <summary>
/// Bridges WFDB records and the app's native pathology dataset.
///
/// WFDB stores raw ADC counts with a per-signal gain (counts per physical unit) and baseline,
/// whereas the app's <see cref="LeadStream"/> stores samples baseline-centered on a fixed value
/// (1024) at a fixed scale (1024 ADC counts/mV — see <see cref="Data.EcgCalibration"/>). This class
/// rescales between the two coordinate systems and maps WFDB signal descriptions to <see cref="Lead"/>s.
/// </summary>
public static class WfdbConverter
{
    /// <summary>App-domain ADC value that represents the isoelectric baseline.</summary>
    public const int DomainBaseline = 1024;

    /// <summary>App-domain ADC counts per millivolt. Must match <see cref="Data.EcgCalibration.AdcCountsPerMv"/>.</summary>
    public const float DomainCountsPerMv = 1024f;

    /// <summary>
    /// Converts a decoded WFDB <paramref name="record"/> into a <see cref="PathologyFile"/>, rescaling
    /// each signal whose description names a known 12-lead lead into the app's baseline-centered units.
    /// Signals that don't map to a lead are skipped.
    /// </summary>
    public static PathologyFile ToPathologyFile(
        WfdbRecord record,
        string id,
        string titleEn,
        string? nameRu = null)
    {
        var leads = new Dictionary<Lead, LeadStream>();
        var signals = record.Header.Signals;

        for (var i = 0; i < signals.Count && i < record.Samples.Length; i++)
        {
            var spec = signals[i];
            if (Leads.FromToken(spec.Description) is not { } lead) continue;
            if (leads.ContainsKey(lead)) continue; // first signal for a lead wins

            var raw = record.Samples[i];
            var gain = spec.EffectiveGain;
            var baseline = spec.EffectiveBaseline;
            var converted = new int[raw.Length];
            for (var s = 0; s < raw.Length; s++)
            {
                var mv = (raw[s] - baseline) / gain;
                converted[s] = (int)Math.Round(DomainBaseline + mv * DomainCountsPerMv);
            }
            leads[lead] = new LeadStream(lead, converted);
        }

        return new PathologyFile(id, titleEn, nameRu, leads);
    }

    /// <summary>
    /// Converts a <paramref name="file"/> into a WFDB record, emitting leads in canonical order.
    /// Samples are rescaled from the app's baseline-centered units back to ADC counts using the
    /// supplied <paramref name="gain"/> and <paramref name="baseline"/>.
    /// </summary>
    public static WfdbRecord FromPathologyFile(
        PathologyFile file,
        double samplingFrequency = 500.0,
        double gain = 1000.0,
        int baseline = 0,
        string units = "mV")
    {
        var orderedLeads = Leads.All.Where(file.Leads.ContainsKey).ToList();
        if (orderedLeads.Count == 0)
            throw new WfdbFormatException($"Pathology '{file.Id}' has no leads to convert.");

        var sampleCount = file.Leads[orderedLeads[0]].Samples.Length;
        var samples = new int[orderedLeads.Count][];
        var specs = new List<WfdbSignalSpec>(orderedLeads.Count);

        for (var i = 0; i < orderedLeads.Count; i++)
        {
            var lead = orderedLeads[i];
            var domain = file.Leads[lead].Samples;
            var raw = new int[domain.Length];
            for (var s = 0; s < domain.Length; s++)
            {
                var mv = (domain[s] - DomainBaseline) / DomainCountsPerMv;
                raw[s] = (int)Math.Round(mv * gain) + baseline;
            }
            samples[i] = raw;

            specs.Add(new WfdbSignalSpec
            {
                FileName = file.Id + ".dat",
                Format = WfdbSignalCodec.Format16,
                Gain = gain,
                Baseline = baseline,
                BaselineSpecified = baseline != 0,
                Units = units,
                AdcResolution = 16,
                AdcZero = 0,
                InitialValue = raw.Length > 0 ? raw[0] : 0,
                Checksum = WfdbWriter.ComputeChecksum(raw),
                Description = lead.ToString(),
            });
        }

        var header = new WfdbHeader
        {
            RecordName = file.Id,
            NumberOfSignals = orderedLeads.Count,
            SamplingFrequency = samplingFrequency,
            NumberOfSamples = sampleCount,
            Signals = specs,
            Comments = BuildComments(file),
        };

        return new WfdbRecord(header, samples);
    }

    private static IReadOnlyList<string> BuildComments(PathologyFile file)
    {
        var comments = new List<string> { $"Title: {file.TitleEn}" };
        if (!string.IsNullOrWhiteSpace(file.NameRu))
            comments.Add($"Name: {file.NameRu}");
        return comments;
    }
}
