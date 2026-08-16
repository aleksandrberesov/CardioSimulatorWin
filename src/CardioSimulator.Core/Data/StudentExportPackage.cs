using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using CardioSimulator.Core.Domain;

namespace CardioSimulator.Core.Data;

/// <summary>
/// Interchange format for importing and exporting student roster entries alongside their saved exam
/// and OSCE assessment results.
/// </summary>
public sealed class StudentExportPackage
{
    [JsonPropertyName("version")]
    public int Version { get; set; } = 1;

    [JsonPropertyName("exportedAt")]
    public DateTimeOffset ExportedAt { get; set; } = DateTimeOffset.Now;

    [JsonPropertyName("students")]
    public List<Student> Students { get; set; } = new();

    [JsonPropertyName("examResults")]
    public List<ExamResult> ExamResults { get; set; } = new();

    [JsonPropertyName("oskeResults")]
    public List<OskeResult> OskeResults { get; set; } = new();

    /// <summary>
    /// Fallback setter for deserializing legacy packages that used the property name "results".
    /// </summary>
    [JsonPropertyName("results")]
    public List<ExamResult>? LegacyResults
    {
        get => null;
        set
        {
            if (value != null && ExamResults.Count == 0)
                ExamResults = value;
        }
    }
}
