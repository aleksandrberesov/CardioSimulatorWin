using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using CardioSimulator.Core.Data;
using CardioSimulator.Core.Domain;

namespace CardioSimulator.App.ViewModels;

/// <summary>The outcome of a <see cref="StudentRegistrationViewModel.Register"/> call, so the screen
/// can report success vs. why an entry was rejected.</summary>
public enum RegisterOutcome
{
    /// <summary>The student was added to the roster.</summary>
    Added,

    /// <summary>Full name and/or group were blank.</summary>
    Invalid,

    /// <summary>A student with the same name + group is already registered.</summary>
    Duplicate,

    /// <summary>The write to disk failed.</summary>
    SaveFailed,
}

/// <summary>
/// Backs the Full-edition Students registration screen: registers new students onto the persisted
/// roster (<see cref="StudentStore"/>) and lists/removes existing ones. The screen re-renders on
/// <see cref="StateChanged"/>. Deliberately thin — validation and de-duplication live in the store/
/// domain so the same rules apply wherever the roster is written.
/// </summary>
public sealed class StudentRegistrationViewModel
{
    private readonly StudentStore _store;
    private readonly ExamResultStore? _examResultStore;
    private readonly OskeResultStore? _oskeResultStore;

    public StudentRegistrationViewModel(
        StudentStore store,
        ExamResultStore? examResultStore = null,
        OskeResultStore? oskeResultStore = null)
    {
        _store = store;
        _examResultStore = examResultStore;
        _oskeResultStore = oskeResultStore;
        Students = _store.List();
    }

    /// <summary>Fires after the roster changes (add/remove) so the screen re-renders.</summary>
    public event Action? StateChanged;

    /// <summary>The current roster, newest first.</summary>
    public IReadOnlyList<Student> Students { get; private set; }

    /// <summary>Registers a new student. Returns why it succeeded or was rejected.</summary>
    public RegisterOutcome Register(string fullName, string group, string? email = null)
    {
        if (Student.Create(fullName, group, email) is not { } student) return RegisterOutcome.Invalid;
        if (_store.Contains(student.FullName, student.Group)) return RegisterOutcome.Duplicate;
        if (!_store.Add(student)) return RegisterOutcome.SaveFailed;
        Refresh();
        return RegisterOutcome.Added;
    }

    /// <summary>Updates an existing student's editable fields. Returns why it succeeded or was rejected,
    /// reusing <see cref="RegisterOutcome"/> (<see cref="RegisterOutcome.Added"/> on success).</summary>
    public RegisterOutcome Update(string id, string fullName, string group, string? email = null)
    {
        fullName = fullName?.Trim() ?? string.Empty;
        group = group?.Trim() ?? string.Empty;
        if (fullName.Length == 0 || group.Length == 0) return RegisterOutcome.Invalid;
        if (_store.ContainsOther(id, fullName, group)) return RegisterOutcome.Duplicate;
        if (!_store.Update(id, fullName, group, email)) return RegisterOutcome.SaveFailed;
        Refresh();
        return RegisterOutcome.Added;
    }

    /// <summary>Removes the student with <paramref name="id"/> from the roster.</summary>
    public void Remove(string id)
    {
        if (_store.Remove(id)) Refresh();
    }

    /// <summary>Exports the student roster along with their recorded assessment results into JSON string.</summary>
    public (int studentCount, int resultCount) ExportData(out string json)
    {
        var students = _store.List().ToList();
        var examResults = _examResultStore?.List() ?? Array.Empty<ExamResult>();
        var oskeResults = _oskeResultStore?.List() ?? Array.Empty<OskeResult>();

        var package = new StudentExportPackage
        {
            Version = 1,
            ExportedAt = DateTimeOffset.Now,
            Students = students,
            ExamResults = examResults.ToList(),
            OskeResults = oskeResults.ToList()
        };

        var totalResults = examResults.Count + oskeResults.Count;
        json = JsonSerializer.Serialize(package, TestJson.Options);
        return (students.Count, totalResults);
    }

    /// <summary>Imports students and assessment results from a JSON string, safely merging new entries.</summary>
    public (bool success, int importedStudents, int importedResults) ImportData(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return (false, 0, 0);

        List<Student> importedStudents = new();
        List<ExamResult> importedExamResults = new();
        List<OskeResult> importedOskeResults = new();

        try
        {
            var package = JsonSerializer.Deserialize<StudentExportPackage>(json, TestJson.Options);
            if (package != null && (package.Students?.Count > 0 || package.ExamResults?.Count > 0 || package.OskeResults?.Count > 0))
            {
                if (package.Students != null) importedStudents.AddRange(package.Students);
                if (package.ExamResults != null) importedExamResults.AddRange(package.ExamResults);
                if (package.OskeResults != null) importedOskeResults.AddRange(package.OskeResults);
            }
            else
            {
                var list = JsonSerializer.Deserialize<List<Student>>(json, TestJson.Options);
                if (list != null && list.Count > 0)
                {
                    importedStudents.AddRange(list);
                }
            }
        }
        catch
        {
            return (false, 0, 0);
        }

        int addedStudents = 0;
        foreach (var s in importedStudents)
        {
            if (s is null || string.IsNullOrWhiteSpace(s.FullName) || string.IsNullOrWhiteSpace(s.Group))
                continue;

            if (!_store.Contains(s.FullName, s.Group))
            {
                if (_store.Add(s))
                    addedStudents++;
            }
            else if (!string.IsNullOrWhiteSpace(s.Email))
            {
                var existing = _store.List().FirstOrDefault(x =>
                    string.Equals(x.FullName?.Trim(), s.FullName?.Trim(), StringComparison.CurrentCultureIgnoreCase) &&
                    string.Equals(x.Group?.Trim(), s.Group?.Trim(), StringComparison.CurrentCultureIgnoreCase));
                if (existing != null && string.IsNullOrWhiteSpace(existing.Email))
                {
                    _store.Update(existing.Id, existing.FullName, existing.Group, s.Email);
                }
            }
        }

        int addedResults = 0;
        if (_examResultStore != null)
        {
            var existingExams = _examResultStore.List();
            foreach (var r in importedExamResults)
            {
                if (r is null || r.Student is null || string.IsNullOrWhiteSpace(r.Student.FullName)) continue;
                var duplicate = existingExams.Any(e =>
                    string.Equals(e.Student.FullName?.Trim(), r.Student.FullName?.Trim(), StringComparison.CurrentCultureIgnoreCase) &&
                    string.Equals(e.Student.Group?.Trim(), r.Student.Group?.Trim(), StringComparison.CurrentCultureIgnoreCase) &&
                    e.TestId == r.TestId &&
                    Math.Abs((e.Timestamp - r.Timestamp).TotalSeconds) < 2);

                if (!duplicate && _examResultStore.Save(r))
                {
                    addedResults++;
                }
            }
        }

        if (_oskeResultStore != null)
        {
            var existingOske = _oskeResultStore.List();
            foreach (var r in importedOskeResults)
            {
                if (r is null || r.Student is null || string.IsNullOrWhiteSpace(r.Student.FullName)) continue;
                var duplicate = existingOske.Any(e =>
                    string.Equals(e.Student.FullName?.Trim(), r.Student.FullName?.Trim(), StringComparison.CurrentCultureIgnoreCase) &&
                    string.Equals(e.Student.Group?.Trim(), r.Student.Group?.Trim(), StringComparison.CurrentCultureIgnoreCase) &&
                    e.EcgId == r.EcgId &&
                    e.FormId == r.FormId &&
                    Math.Abs((e.Timestamp - r.Timestamp).TotalSeconds) < 2);

                if (!duplicate && _oskeResultStore.Save(r))
                {
                    addedResults++;
                }
            }
        }

        Refresh();
        return (true, addedStudents, addedResults);
    }

    private void Refresh()
    {
        Students = _store.List();
        StateChanged?.Invoke();
    }
}

