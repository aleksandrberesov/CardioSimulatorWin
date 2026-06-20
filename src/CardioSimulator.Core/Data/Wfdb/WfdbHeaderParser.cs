using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace CardioSimulator.Core.Data.Wfdb;

/// <summary>Thrown when a WFDB header cannot be parsed.</summary>
public sealed class WfdbFormatException : Exception
{
    public WfdbFormatException(string message, Exception? cause = null) : base(message, cause) { }
}

/// <summary>
/// Parses and serializes WFDB <c>.hea</c> headers.
///
/// Header layout (one record line, then one line per signal, then comment lines):
/// <code>
/// JS00001 12 500 5000
/// JS00001.mat 16+24 1000/mV 16 0 -254 21756 0 I
/// ...
/// #Age: 85
/// </code>
/// Record line: <c>name nsig [fs] [nsamp] [basetime] [basedate]</c>.
/// Signal line: <c>file format[xN][:skew][+offset] gain[(baseline)]/units adcres adczero initval checksum blocksize description</c>.
/// </summary>
public static class WfdbHeaderParser
{
    private static readonly Regex FormatSpecRegex = new(
        @"^(?<fmt>\d+)(x(?<spf>\d+))?(:(?<skew>\d+))?(\+(?<off>\d+))?$",
        RegexOptions.Compiled);

    private static readonly Regex GainSpecRegex = new(
        @"^(?<gain>[-+]?[0-9]*\.?[0-9]+)(\((?<base>[-+]?\d+)\))?(/(?<units>.+))?$",
        RegexOptions.Compiled);

    public static WfdbHeader Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new WfdbFormatException("Empty WFDB header.");

        var rawLines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

        string? recordLine = null;
        var signalLines = new List<string>();
        var comments = new List<string>();

        foreach (var raw in rawLines)
        {
            var line = raw.TrimEnd();
            if (line.Length == 0) continue;
            if (line.StartsWith('#'))
            {
                comments.Add(line[1..].TrimStart());
                continue;
            }
            if (recordLine is null)
            {
                recordLine = line;
            }
            else
            {
                signalLines.Add(line);
            }
        }

        if (recordLine is null)
            throw new WfdbFormatException("WFDB header has no record line.");

        var (recordName, nsig, fs, nsamp, baseTime, baseDate) = ParseRecordLine(recordLine);

        var signals = new List<WfdbSignalSpec>(nsig);
        // Tolerate fewer/more signal lines than declared; parse what is present.
        var toParse = nsig > 0 ? Math.Min(nsig, signalLines.Count) : signalLines.Count;
        for (var i = 0; i < toParse; i++)
        {
            signals.Add(ParseSignalLine(signalLines[i]));
        }

        return new WfdbHeader
        {
            RecordName = recordName,
            NumberOfSignals = nsig,
            SamplingFrequency = fs,
            NumberOfSamples = nsamp,
            BaseTime = baseTime,
            BaseDate = baseDate,
            Signals = signals,
            Comments = comments,
        };
    }

    private static (string name, int nsig, double fs, long nsamp, string? baseTime, string? baseDate)
        ParseRecordLine(string line)
    {
        var tokens = SplitWhitespace(line);
        if (tokens.Length < 2)
            throw new WfdbFormatException($"Invalid WFDB record line: '{line}'.");

        // The record name may carry a "/segments" suffix for multi-segment records; we keep only the name.
        var name = tokens[0].Split('/', 2)[0];

        if (!int.TryParse(tokens[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var nsig))
            throw new WfdbFormatException($"Invalid signal count in record line: '{line}'.");

        var fs = WfdbConstants.DefaultSamplingFrequency;
        if (tokens.Length >= 3)
        {
            // fs may be "500", "500/1000" (with counter frequency), or "500/1000:0".
            var fsToken = tokens[2].Split('/', 2)[0];
            if (double.TryParse(fsToken, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedFs))
                fs = parsedFs;
        }

        long nsamp = 0;
        if (tokens.Length >= 4)
            long.TryParse(tokens[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out nsamp);

        var baseTime = tokens.Length >= 5 ? tokens[4] : null;
        var baseDate = tokens.Length >= 6 ? tokens[5] : null;

        return (name, nsig, fs, nsamp, baseTime, baseDate);
    }

    private static WfdbSignalSpec ParseSignalLine(string line)
    {
        // Description (last field) may contain spaces, so split into at most 9 fields.
        var tokens = line.Split((char[]?)null, 9, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 2)
            throw new WfdbFormatException($"Invalid WFDB signal line: '{line}'.");

        var fileName = tokens[0];
        var (format, spf, skew, offset) = ParseFormatSpec(tokens[1]);

        var spec = new WfdbSignalSpec
        {
            FileName = fileName,
            Format = format,
            SamplesPerFrame = spf,
            Skew = skew,
            ByteOffset = offset,
        };

        if (tokens.Length >= 3)
        {
            var (gain, baseline, baselineSpecified, units) = ParseGainSpec(tokens[2]);
            spec = spec with
            {
                Gain = gain,
                Baseline = baseline,
                BaselineSpecified = baselineSpecified,
                Units = units,
            };
        }

        if (tokens.Length >= 4 && int.TryParse(tokens[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var adcRes))
            spec = spec with { AdcResolution = adcRes };
        if (tokens.Length >= 5 && int.TryParse(tokens[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var adcZero))
            spec = spec with { AdcZero = adcZero };
        if (tokens.Length >= 6 && int.TryParse(tokens[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out var initVal))
            spec = spec with { InitialValue = initVal };
        if (tokens.Length >= 7 && int.TryParse(tokens[6], NumberStyles.Integer, CultureInfo.InvariantCulture, out var checksum))
            spec = spec with { Checksum = checksum };
        if (tokens.Length >= 8 && int.TryParse(tokens[7], NumberStyles.Integer, CultureInfo.InvariantCulture, out var blockSize))
            spec = spec with { BlockSize = blockSize };
        if (tokens.Length >= 9)
            spec = spec with { Description = tokens[8].Trim() };

        // When no explicit baseline was given, WFDB uses the ADC zero as the baseline.
        if (!spec.BaselineSpecified)
            spec = spec with { Baseline = spec.AdcZero };

        return spec;
    }

    private static (int format, int spf, int skew, long offset) ParseFormatSpec(string token)
    {
        var m = FormatSpecRegex.Match(token);
        if (!m.Success)
            throw new WfdbFormatException($"Invalid WFDB format spec: '{token}'.");

        var format = int.Parse(m.Groups["fmt"].Value, CultureInfo.InvariantCulture);
        var spf = m.Groups["spf"].Success ? int.Parse(m.Groups["spf"].Value, CultureInfo.InvariantCulture) : 1;
        var skew = m.Groups["skew"].Success ? int.Parse(m.Groups["skew"].Value, CultureInfo.InvariantCulture) : 0;
        var offset = m.Groups["off"].Success ? long.Parse(m.Groups["off"].Value, CultureInfo.InvariantCulture) : 0L;
        return (format, spf, skew, offset);
    }

    private static (double gain, int baseline, bool baselineSpecified, string units) ParseGainSpec(string token)
    {
        var m = GainSpecRegex.Match(token);
        if (!m.Success)
        {
            // Some headers carry just units (no gain) — treat as uncalibrated.
            return (0, 0, false, token);
        }

        var gain = double.Parse(m.Groups["gain"].Value, CultureInfo.InvariantCulture);
        var baselineSpecified = m.Groups["base"].Success;
        var baseline = baselineSpecified ? int.Parse(m.Groups["base"].Value, CultureInfo.InvariantCulture) : 0;
        var units = m.Groups["units"].Success ? m.Groups["units"].Value.Trim() : WfdbConstants.DefaultUnits;
        return (gain, baseline, baselineSpecified, units);
    }

    public static string Serialize(WfdbHeader header)
    {
        var sb = new StringBuilder();

        sb.Append(header.RecordName)
          .Append(' ').Append(header.NumberOfSignals.ToString(CultureInfo.InvariantCulture))
          .Append(' ').Append(FormatNumber(header.SamplingFrequency))
          .Append(' ').Append(header.NumberOfSamples.ToString(CultureInfo.InvariantCulture));
        if (!string.IsNullOrEmpty(header.BaseTime))
            sb.Append(' ').Append(header.BaseTime);
        if (!string.IsNullOrEmpty(header.BaseDate))
            sb.Append(' ').Append(header.BaseDate);
        sb.Append('\n');

        foreach (var s in header.Signals)
            sb.Append(SerializeSignal(s)).Append('\n');

        foreach (var c in header.Comments)
            sb.Append('#').Append(c).Append('\n');

        return sb.ToString();
    }

    private static string SerializeSignal(WfdbSignalSpec s)
    {
        var sb = new StringBuilder();
        sb.Append(s.FileName).Append(' ');

        sb.Append(s.Format.ToString(CultureInfo.InvariantCulture));
        if (s.SamplesPerFrame > 1) sb.Append('x').Append(s.SamplesPerFrame.ToString(CultureInfo.InvariantCulture));
        if (s.Skew > 0) sb.Append(':').Append(s.Skew.ToString(CultureInfo.InvariantCulture));
        if (s.ByteOffset > 0) sb.Append('+').Append(s.ByteOffset.ToString(CultureInfo.InvariantCulture));

        sb.Append(' ').Append(FormatNumber(s.Gain));
        if (s.BaselineSpecified) sb.Append('(').Append(s.Baseline.ToString(CultureInfo.InvariantCulture)).Append(')');
        sb.Append('/').Append(s.Units);

        sb.Append(' ').Append(s.AdcResolution.ToString(CultureInfo.InvariantCulture));
        sb.Append(' ').Append(s.AdcZero.ToString(CultureInfo.InvariantCulture));
        sb.Append(' ').Append(s.InitialValue.ToString(CultureInfo.InvariantCulture));
        sb.Append(' ').Append(s.Checksum.ToString(CultureInfo.InvariantCulture));
        sb.Append(' ').Append(s.BlockSize.ToString(CultureInfo.InvariantCulture));
        if (!string.IsNullOrEmpty(s.Description))
            sb.Append(' ').Append(s.Description);

        return sb.ToString();
    }

    private static string FormatNumber(double value) =>
        value.ToString("0.######", CultureInfo.InvariantCulture);

    private static string[] SplitWhitespace(string line) =>
        line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
}
