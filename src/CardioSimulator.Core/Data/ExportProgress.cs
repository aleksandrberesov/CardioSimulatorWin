namespace CardioSimulator.Core.Data;

/// <summary>
/// A snapshot of an in-flight content-pack export, reported per entry as
/// <see cref="ContentPackWriter.WriteEncryptedPack"/> streams the dataset out. The total entry count
/// is not known up front (entries are enumerated lazily to keep peak memory bounded), so consumers
/// show a running tally rather than a percentage.
/// </summary>
/// <param name="EntriesWritten">Number of entries flushed to the pack so far.</param>
/// <param name="BytesWritten">Uncompressed bytes written so far (sum of entry payloads).</param>
/// <param name="CurrentEntry">Path of the entry just written, or <c>null</c> before the first entry.</param>
public readonly record struct ExportProgress(int EntriesWritten, long BytesWritten, string? CurrentEntry);
