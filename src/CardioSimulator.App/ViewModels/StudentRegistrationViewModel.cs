using System;
using System.Collections.Generic;
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

    public StudentRegistrationViewModel(StudentStore store)
    {
        _store = store;
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

    private void Refresh()
    {
        Students = _store.List();
        StateChanged?.Invoke();
    }
}
