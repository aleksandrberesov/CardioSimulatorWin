using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using CardioSimulator.Core.Domain;

namespace CardioSimulator.Core.Data;

/// <summary>
/// Persists the instructor's student roster as a single JSON array at <see cref="Path"/>
/// (<c>students.json</c>). One file (not one-per-entry) keeps the whole list ordered and cheap to read
/// back for the registration screen and the exam start pick-list. Writes are atomic (temp + move) and
/// relaxed-escaped so Cyrillic names are written literally.
/// </summary>
public sealed class StudentStore
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public string Path { get; }

    public StudentStore(string path)
    {
        Path = path;
    }

    /// <summary>The roster, newest registration first. Empty when none is on disk / unreadable.</summary>
    public IReadOnlyList<Student> List()
    {
        try
        {
            if (!File.Exists(Path)) return Array.Empty<Student>();
            var list = JsonSerializer.Deserialize<List<Student>>(File.ReadAllText(Path, Encoding.UTF8), JsonOptions);
            return (list ?? new List<Student>())
                .Where(s => s is not null && !string.IsNullOrWhiteSpace(s.FullName))
                .OrderByDescending(s => s.RegisteredAt)
                .ToList();
        }
        catch { return Array.Empty<Student>(); }
    }

    /// <summary>Adds <paramref name="student"/> unless a same-name-and-group entry already exists
    /// (case-insensitive). Returns true when the roster was actually changed.</summary>
    public bool Add(Student student)
    {
        if (student is null || string.IsNullOrWhiteSpace(student.FullName)) return false;
        var list = List().ToList();
        if (list.Any(s => IsSamePerson(s, student))) return false;
        list.Add(student);
        return Write(list);
    }

    /// <summary>Updates the mutable fields (full name / group / e-mail) of the entry with
    /// <paramref name="id"/>, keeping its original registration time. Returns true when the roster was
    /// changed. No-ops (false) when the id is missing or the new name/group are blank; callers that need
    /// to reject a rename onto another entry should check <see cref="ContainsOther"/> first.</summary>
    public bool Update(string id, string fullName, string group, string? email)
    {
        fullName = fullName?.Trim() ?? string.Empty;
        group = group?.Trim() ?? string.Empty;
        if (fullName.Length == 0 || group.Length == 0) return false;
        var list = List().ToList();
        var idx = list.FindIndex(s => s.Id == id);
        if (idx < 0) return false;
        var trimmedEmail = string.IsNullOrWhiteSpace(email) ? null : email.Trim();
        list[idx] = list[idx] with { FullName = fullName, Group = group, Email = trimmedEmail };
        return Write(list);
    }

    /// <summary>Removes the entry with <paramref name="id"/>. Returns true when one was removed.</summary>
    public bool Remove(string id)
    {
        var list = List().ToList();
        var removed = list.RemoveAll(s => s.Id == id) > 0;
        return removed && Write(list);
    }

    /// <summary>True when a student with the same full name + group is already on the roster.</summary>
    public bool Contains(string fullName, string group)
    {
        var probe = new Student(string.Empty, fullName?.Trim() ?? string.Empty, group?.Trim() ?? string.Empty, null, default);
        return List().Any(s => IsSamePerson(s, probe));
    }

    /// <summary>True when a <em>different</em> student (id ≠ <paramref name="exceptId"/>) already has the
    /// same full name + group — guards an edit from colliding with another roster entry.</summary>
    public bool ContainsOther(string exceptId, string fullName, string group)
    {
        var probe = new Student(string.Empty, fullName?.Trim() ?? string.Empty, group?.Trim() ?? string.Empty, null, default);
        return List().Any(s => s.Id != exceptId && IsSamePerson(s, probe));
    }

    private static bool IsSamePerson(Student a, Student b) =>
        string.Equals(a.FullName?.Trim(), b.FullName?.Trim(), StringComparison.CurrentCultureIgnoreCase) &&
        string.Equals(a.Group?.Trim(), b.Group?.Trim(), StringComparison.CurrentCultureIgnoreCase);

    private bool Write(IEnumerable<Student> students)
    {
        try
        {
            var dir = System.IO.Path.GetDirectoryName(Path);
            if (dir != null && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            var ordered = students.OrderByDescending(s => s.RegisteredAt).ToList();
            var tmp = Path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(ordered, JsonOptions), Utf8NoBom);
            if (File.Exists(Path)) File.Delete(Path);
            File.Move(tmp, Path);
            return true;
        }
        catch { return false; }
    }
}
