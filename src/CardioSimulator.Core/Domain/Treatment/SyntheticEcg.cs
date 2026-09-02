using System;
using System.Collections.Generic;

namespace CardioSimulator.Core.Domain.Treatment;

/// <summary>
/// Procedurally generates ECG waveforms for peri-arrest rhythms that have no authored <c>.dat</c> in the
/// content pak, so the treatment panel can display a clinically recognizable trace instead of a wrong
/// substitute. Pure/data-only (no rendering, no pak I/O) → unit-testable. Output samples are baseline-zeroed
/// ADC counts, the same units/scale as a real waveform (sample value / <c>adcCountsPerMv</c> = millivolts).
/// </summary>
public static class SyntheticEcg
{
    // Frontal-plane (hexaxial) axis of each limb lead; the precordials are transverse-plane, so they are given
    // an approximate spread around the circle purely so each one twists with its own offset. Degrees.
    private static double AxisRadians(Lead lead) => (lead switch
    {
        Lead.I => 0, Lead.II => 60, Lead.III => 120,
        Lead.aVR => 210, Lead.aVL => -30, Lead.aVF => 90,
        Lead.V1 => 200, Lead.V2 => 245, Lead.V3 => 290,
        Lead.V4 => 335, Lead.V5 => 25, Lead.V6 => 65,
        _ => 0,
    }) * Math.PI / 180.0;

    // Relative per-lead amplitude (precordials generally larger).
    private static double Gain(Lead lead) => lead switch
    {
        Lead.I => 0.8, Lead.II => 1.0, Lead.III => 0.8,
        Lead.aVR => 0.7, Lead.aVL => 0.7, Lead.aVF => 0.9,
        Lead.V1 => 0.9, Lead.V2 => 1.1, Lead.V3 => 1.2,
        Lead.V4 => 1.2, Lead.V5 => 1.0, Lead.V6 => 0.9,
        _ => 1.0,
    };

    /// <summary>Peak per-lead QRS deflection (mV) when a lead is momentarily aligned with the heart vector.</summary>
    public const double PeakMv = 1.2;

    /// <summary>
    /// Torsades de pointes / polymorphic VT: a fast (~250 bpm) run of ventricular complexes whose amplitude
    /// waxes and wanes and whose polarity twists around the baseline ("spindle"). Modelled as a heart vector
    /// that pulses at the ventricular rate while its direction slowly rotates — each lead sees the projection
    /// of that rotating vector onto its own axis, so the spindle nodes and polarity flips fall at different
    /// times per lead, which is the real mechanism.
    /// </summary>
    /// <param name="leads">Leads to synthesize (canonical order preserved by the caller).</param>
    /// <param name="sampleRateHz">Sampling rate of the target monitor (Hz).</param>
    /// <param name="sampleCount">Samples per lead (strip length = sampleCount / sampleRateHz seconds).</param>
    /// <param name="adcCountsPerMv">ADC counts per millivolt, from the active calibration.</param>
    public static IReadOnlyDictionary<Lead, float[]> Torsades(
        IReadOnlyList<Lead> leads, int sampleRateHz, int sampleCount, float adcCountsPerMv)
    {
        if (leads is null) throw new ArgumentNullException(nameof(leads));
        if (sampleRateHz <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRateHz));
        if (sampleCount < 0) throw new ArgumentOutOfRangeException(nameof(sampleCount));

        const double beatRateHz = 250.0 / 60.0; // ~250 bpm ventricular rate
        const double twistPeriodSec = 2.8;      // one full 360° axis rotation (~11-12 complexes)
        const double sharp = 2.2;               // QRS-bump peakiness (broad-but-peaked wide-complex look)
        var peakAdc = PeakMv * adcCountsPerMv;

        // Shared, lead-independent series: the QRS-magnitude bump train and the slow rotation angle.
        var mag = new double[sampleCount];
        var theta = new double[sampleCount];
        double beatPhase = 0;
        for (var i = 0; i < sampleCount; i++)
        {
            var t = i / (double)sampleRateHz;
            // Small, slow rate wobble so the run is not mechanically periodic.
            var rate = beatRateHz * (1.0 + 0.08 * Math.Sin(2 * Math.PI * 0.11 * t));
            beatPhase += rate / sampleRateHz;
            var frac = beatPhase - Math.Floor(beatPhase);              // 0..1 within a complex
            mag[i] = Math.Pow(Math.Abs(Math.Sin(Math.PI * frac)), sharp); // 0..1 activation bump
            theta[i] = 2 * Math.PI * t / twistPeriodSec;              // slowly rotating vector angle
        }

        var result = new Dictionary<Lead, float[]>(leads.Count);
        foreach (var lead in leads)
        {
            var axis = AxisRadians(lead);
            var gain = Gain(lead);
            var buf = new float[sampleCount];
            for (var i = 0; i < sampleCount; i++)
                buf[i] = (float)(peakAdc * gain * mag[i] * Math.Cos(theta[i] - axis));
            result[lead] = buf;
        }
        return result;
    }
}
