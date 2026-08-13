using System;

namespace CardioSimulator.Core.Domain;

/// <summary>
/// A registered student on the instructor's roster — persisted by
/// <see cref="CardioSimulator.Core.Data.StudentStore"/> and offered as a pick-list when starting an
/// examination. <see cref="FullName"/> + <see cref="Group"/> map onto the ad-hoc
/// <see cref="ExamStudentInfo"/> the exam pipeline already grades against; the roster simply lets a
/// teacher enter them once instead of re-typing per attempt. <see cref="Id"/> is a stable GUID string
/// (the delete/lookup key), <see cref="Email"/> is optional, and <see cref="RegisteredAt"/> records
/// when the entry was added.
/// </summary>
public sealed record Student(
    string Id,
    string FullName,
    string Group,
    string? Email,
    DateTimeOffset RegisteredAt)
{
    /// <summary>The exam-pipeline identity for this student (ФИО + группа).</summary>
    public ExamStudentInfo ToExamInfo() => new(FullName, Group);

    /// <summary>Creates a roster entry with a fresh id/timestamp from trimmed input. Returns null when
    /// the required fields (full name, group) are blank.</summary>
    public static Student? Create(string fullName, string group, string? email = null)
    {
        fullName = fullName?.Trim() ?? string.Empty;
        group = group?.Trim() ?? string.Empty;
        if (fullName.Length == 0 || group.Length == 0) return null;
        var trimmedEmail = string.IsNullOrWhiteSpace(email) ? null : email.Trim();
        return new Student(Guid.NewGuid().ToString("N"), fullName, group, trimmedEmail, DateTimeOffset.Now);
    }
}
