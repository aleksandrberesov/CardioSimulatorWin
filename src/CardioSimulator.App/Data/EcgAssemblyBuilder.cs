using System;
using System.Collections.Generic;
using System.Linq;
using BioSPPy.Net.Signals.Ecg;
using CardioSimulator.Core.Data;
using CardioSimulator.Core.Domain;

namespace CardioSimulator.App.Data;

/// <summary>
/// Builds an <see cref="EcgAssembly"/> for the «Собери ЭКГ» question type by slicing real rhythms into
/// P / QRS / T fragments at <em>authoring</em> time — the target rhythm supplies the correct pieces,
/// each distractor rhythm supplies the wrong ones. Runs entirely in the App layer because it uses the
/// BioSPPy fiducial detector (the same pipeline the Constructor's Auto-Detect uses); the sliced pieces
/// are then stored on the question so the Testing runtime needs no signal processing.
/// </summary>
public static class EcgAssemblyBuilder
{
    private const int DefaultBaseline = 1024;

    /// <summary>
    /// Assembles the spec: slices the target and each (deduped, non-self) distractor's chosen lead into
    /// P/QRS/T, then groups them by wave. Returns null if the <em>target</em> can't be sliced (no usable
    /// beat); distractors that fail to slice are simply dropped.
    /// </summary>
    public static EcgAssembly? Build(
        PathologyRepository repository,
        string targetId,
        IReadOnlyList<string> distractorIds,
        Lead lead,
        float sampleRateHz)
    {
        if (repository is null || string.IsNullOrWhiteSpace(targetId)) return null;

        var target = SlicePathology(repository, targetId, lead, sampleRateHz);
        if (target is null) return null;

        var distractors = new List<(string Id, (int[] P, int[] Qrs, int[] T) Slice)>();
        foreach (var id in distractorIds?.Where(x => !string.IsNullOrWhiteSpace(x) && x != targetId).Distinct()
                           ?? Enumerable.Empty<string>())
        {
            if (SlicePathology(repository, id, lead, sampleRateHz) is { } slice)
                distractors.Add((id, slice));
        }

        EcgAssemblyBlock MakeBlock(EcgBlock block, Func<(int[] P, int[] Qrs, int[] T), int[]> pick) => new(
            block,
            new EcgBlockPiece(block, pick(target.Value), targetId),
            distractors.Select(d => new EcgBlockPiece(block, pick(d.Slice), d.Id)).ToList());

        var blocks = new List<EcgAssemblyBlock>
        {
            MakeBlock(EcgBlock.P, s => s.P),
            MakeBlock(EcgBlock.QRS, s => s.Qrs),
            MakeBlock(EcgBlock.T, s => s.T),
        };

        return new EcgAssembly(
            SampleRateHz: (int)MathF.Round(sampleRateHz),
            Blocks: blocks,
            TargetPathologyId: targetId,
            DistractorPathologyIds: distractors.Select(d => d.Id).ToList(),
            SliceLead: lead);
    }

    /// <summary>
    /// Slices one pathology's chosen lead into baseline-zeroed P/QRS/T segments. Prefers the pathology's
    /// hand-authored markers; falls back to BioSPPy auto-detection when they don't frame a usable beat.
    /// Returns null when neither yields a complete P→QRS→T beat.
    /// </summary>
    public static (int[] P, int[] Qrs, int[] T)? SlicePathology(
        PathologyRepository repository, string id, Lead lead, float sampleRateHz)
    {
        var file = repository.ReadPathology(id);
        if (file is null) return null;
        var baseline = repository.Manifest()?.Baseline ?? DefaultBaseline;

        int[] samples;
        int sliceBaseline;
        IReadOnlyList<SignificantPoint> markers;

        if (file.Leads.TryGetValue(lead, out var stream))
        {
            samples = stream.Samples;
            sliceBaseline = baseline;
            markers = file.SignificantPoints;
        }
        else
        {
            // Lead not physically stored (e.g. a derived lead): use the already baseline-subtracted
            // waveform and detect fresh — stored markers index the stored leads, not a derived one.
            var wf = repository.LeadWaveform(id, lead);
            if (wf is null) return null;
            samples = wf.Values.Select(v => (int)MathF.Round(v)).ToArray();
            sliceBaseline = 0;
            markers = Array.Empty<SignificantPoint>();
        }

        var beat = EcgBeatSlicer.BestBeat(markers, samples.Length)
                   ?? EcgBeatSlicer.BestBeat(Detect(samples, sliceBaseline, sampleRateHz), samples.Length);
        return beat is null ? null : EcgBeatSlicer.Slice(samples, sliceBaseline, beat.Value);
    }

    /// <summary>Runs the BioSPPy R-peak + landmark pipeline and flattens the result into markers.</summary>
    private static IReadOnlyList<SignificantPoint> Detect(int[] samples, int baseline, float sampleRateHz)
    {
        if (samples.Length < 3 || sampleRateHz <= 0) return Array.Empty<SignificantPoint>();

        double fs = sampleRateHz;
        var signal = samples.Select(x => (double)(x - baseline)).ToArray();

        int[] rpeaks;
        try
        {
            rpeaks = QrsSegmenters.HamiltonSegmenter(signal, fs);
            rpeaks = QrsSegmenters.CorrectRPeaks(signal, rpeaks, fs, 0.05);
        }
        catch
        {
            return Array.Empty<SignificantPoint>();
        }
        if (rpeaks.Length == 0) return Array.Empty<SignificantPoint>();

        var points = new List<SignificantPoint>();
        foreach (var lm in FiducialPoints.GetLandmarks(signal, rpeaks, fs))
        {
            if (lm.PStart != -1) points.Add(new SignificantPoint(lm.PStart, EcgPointType.P_START));
            if (lm.PEnd != -1) points.Add(new SignificantPoint(lm.PEnd, EcgPointType.P_END));
            if (lm.QrsStart != -1) points.Add(new SignificantPoint(lm.QrsStart, EcgPointType.QRS_START));
            if (lm.QrsEnd != -1) points.Add(new SignificantPoint(lm.QrsEnd, EcgPointType.QRS_END));
            if (lm.TStart != -1) points.Add(new SignificantPoint(lm.TStart, EcgPointType.T_START));
            if (lm.TEnd != -1) points.Add(new SignificantPoint(lm.TEnd, EcgPointType.T_END));
        }
        return points;
    }
}
