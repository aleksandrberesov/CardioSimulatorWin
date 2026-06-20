namespace CardioSimulator.Core.Data.Wfdb;

/// <summary>
/// Shared constants for the WFDB (WaveForm DataBase) format used by PhysioNet.
/// The format is documented at https://physionet.org/physiotools/wag/header-5.htm
/// and https://physionet.org/physiotools/wag/signal-5.htm.
/// </summary>
public static class WfdbConstants
{
    /// <summary>ADC gain (units per physical unit) assumed when a signal declares gain 0 ("uncalibrated").</summary>
    public const double DefaultGain = 200.0;

    /// <summary>Default sampling frequency when the header omits it.</summary>
    public const double DefaultSamplingFrequency = 250.0;

    /// <summary>Default physical units when a signal omits them.</summary>
    public const string DefaultUnits = "mV";

    /// <summary>Name of the single matrix variable inside a wfdb2mat-produced <c>.mat</c> file.</summary>
    public const string MatVariableName = "val";
}

/// <summary>
/// One signal specification line from a WFDB <c>.hea</c> header, e.g.
/// <c>JS00001.mat 16+24 1000/mV 16 0 -254 21756 0 I</c>.
/// Fields after the format are optional in the spec; defaults follow the WFDB conventions.
/// </summary>
public sealed record WfdbSignalSpec
{
    /// <summary>Name of the file holding this signal's samples, relative to the header.</summary>
    public required string FileName { get; init; }

    /// <summary>Storage format code (16, 61, 80, 212, 24, 32, ...).</summary>
    public int Format { get; init; } = 16;

    /// <summary>Samples per frame for this signal (the <c>xN</c> modifier); 1 for the usual case.</summary>
    public int SamplesPerFrame { get; init; } = 1;

    /// <summary>Inter-signal skew in frames (the <c>:N</c> modifier); rarely used.</summary>
    public int Skew { get; init; }

    /// <summary>Byte offset to the first sample within <see cref="FileName"/> (the <c>+N</c> modifier).</summary>
    public long ByteOffset { get; init; }

    /// <summary>ADC units per physical unit. 0 means "uncalibrated" — see <see cref="WfdbConstants.DefaultGain"/>.</summary>
    public double Gain { get; init; } = WfdbConstants.DefaultGain;

    /// <summary>ADC value that corresponds to 0 physical units. Defaults to <see cref="AdcZero"/> when unspecified.</summary>
    public int Baseline { get; init; }

    /// <summary>True when the header carried an explicit baseline in the gain field (<c>gain(baseline)/units</c>).</summary>
    public bool BaselineSpecified { get; init; }

    /// <summary>Physical units string (e.g. <c>mV</c>).</summary>
    public string Units { get; init; } = WfdbConstants.DefaultUnits;

    /// <summary>ADC resolution in bits.</summary>
    public int AdcResolution { get; init; } = 16;

    /// <summary>ADC value for a zero-input (mid-range) reading.</summary>
    public int AdcZero { get; init; }

    /// <summary>First sample value, used as a sanity check against the decoded signal.</summary>
    public int InitialValue { get; init; }

    /// <summary>16-bit checksum of all samples (low 16 bits of their sum, as a signed value).</summary>
    public int Checksum { get; init; }

    /// <summary>Reading block size in bytes (0 = unspecified).</summary>
    public int BlockSize { get; init; }

    /// <summary>Human-readable signal description, typically the lead name (e.g. <c>II</c>, <c>V1</c>).</summary>
    public string Description { get; init; } = "";

    /// <summary>Baseline to use for physical conversion: the explicit baseline, else the ADC zero.</summary>
    public int EffectiveBaseline => BaselineSpecified ? Baseline : AdcZero;

    /// <summary>Gain to use for physical conversion: the declared gain, or the default when uncalibrated.</summary>
    public double EffectiveGain => Gain == 0 ? WfdbConstants.DefaultGain : Gain;
}

/// <summary>
/// A parsed WFDB header (<c>.hea</c>): the record line plus its signal specifications and comments.
/// </summary>
public sealed record WfdbHeader
{
    /// <summary>Record name (the header's base filename without extension).</summary>
    public required string RecordName { get; init; }

    /// <summary>Number of signals (channels) in the record.</summary>
    public int NumberOfSignals { get; init; }

    /// <summary>Sampling frequency in Hz.</summary>
    public double SamplingFrequency { get; init; } = WfdbConstants.DefaultSamplingFrequency;

    /// <summary>Number of samples per signal. 0 means "unspecified / read to end of file".</summary>
    public long NumberOfSamples { get; init; }

    /// <summary>Optional base time (<c>HH:MM:SS</c>) from the record line.</summary>
    public string? BaseTime { get; init; }

    /// <summary>Optional base date (<c>DD/MM/YYYY</c>) from the record line.</summary>
    public string? BaseDate { get; init; }

    /// <summary>Per-signal specifications, in declaration order.</summary>
    public IReadOnlyList<WfdbSignalSpec> Signals { get; init; } = Array.Empty<WfdbSignalSpec>();

    /// <summary>Comment lines (the text after each leading <c>#</c>), preserved verbatim for round-tripping.</summary>
    public IReadOnlyList<string> Comments { get; init; } = Array.Empty<string>();
}

/// <summary>
/// A fully decoded WFDB record: its header plus raw ADC samples laid out as
/// <c>Samples[signalIndex][sampleIndex]</c>.
/// </summary>
public sealed record WfdbRecord(WfdbHeader Header, int[][] Samples)
{
    /// <summary>Number of channels (signals).</summary>
    public int ChannelCount => Samples.Length;

    /// <summary>Number of samples per channel.</summary>
    public int SampleCount => Samples.Length > 0 ? Samples[0].Length : 0;
}
