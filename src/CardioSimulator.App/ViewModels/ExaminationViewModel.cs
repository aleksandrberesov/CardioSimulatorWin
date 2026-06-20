using System;
using System.Collections.Generic;
using CardioSimulator.Core.Data;
using CardioSimulator.Core.Domain;

namespace CardioSimulator.App.ViewModels;

/// <summary>
/// Drives one examination attempt over a <see cref="Test"/> from the shared test bank. Unlike the
/// self-assessment <see cref="TestViewModel"/>, answers are recorded without per-question feedback (no
/// comment, no ✓/✗) and the whole attempt is graded + saved at the end via <see cref="ExamGrader"/> /
/// <see cref="ExamResultStore"/> — the OSCE-style result pipeline. <see cref="StateChanged"/> drives a
/// full re-render; <see cref="TimerTicked"/> updates only the countdown label.
/// </summary>
public sealed class ExaminationViewModel
{
    private readonly ExamResultStore _resultStore;
    private readonly Dictionary<string, string> _selections = new(); // questionId -> optionId

    public ExaminationViewModel(ExamResultStore resultStore)
    {
        _resultStore = resultStore;
    }

    public Test? Test { get; private set; }
    public ExamStudentInfo? Student { get; private set; }
    public int Index { get; private set; }
    public int RemainingSeconds { get; private set; }
    public ExamResult? Result { get; private set; }

    public event Action? StateChanged;
    public event Action? TimerTicked;

    public int Count => Test?.Questions.Count ?? 0;

    public TestQuestion? Current =>
        Test is not null && Index >= 0 && Index < Test.Questions.Count ? Test.Questions[Index] : null;

    /// <summary>True while an attempt is in progress (started and not yet graded).</summary>
    public bool IsTakingExam => Test is not null && Result is null;

    public bool IsTimed => (Test?.QuestionTimeSeconds ?? 0) > 0;

    public bool IsLastQuestion => Test is not null && Index + 1 >= Test.Questions.Count;

    public string? SelectedFor(string questionId) =>
        _selections.TryGetValue(questionId, out var v) ? v : null;

    public void Start(Test test, ExamStudentInfo student)
    {
        Test = test;
        Student = student;
        Index = 0;
        _selections.Clear();
        Result = null;
        BeginQuestion();
    }

    private void BeginQuestion()
    {
        RemainingSeconds = Test?.QuestionTimeSeconds ?? 0;
        StateChanged?.Invoke();
    }

    /// <summary>Records (or changes) the answer to the current question. No feedback is shown.</summary>
    public void Select(string optionId)
    {
        if (Current is null || Result is not null) return;
        _selections[Current.Id] = optionId;
        StateChanged?.Invoke();
    }

    /// <summary>Advances to the next question, or grades + saves the attempt on the last one.</summary>
    public void Next()
    {
        if (Test is null || Result is not null) return;
        if (IsLastQuestion)
        {
            Submit();
            return;
        }
        Index++;
        BeginQuestion();
    }

    public ExamResult? Submit()
    {
        if (Test is null || Student is null || Result is not null) return null;
        var result = ExamGrader.Grade(Test, _selections, Student);
        _resultStore.Save(result);
        Result = result;
        StateChanged?.Invoke();
        return result;
    }

    /// <summary>Clears the attempt back to the pre-start state.</summary>
    public void Reset()
    {
        Test = null;
        Student = null;
        Index = 0;
        _selections.Clear();
        Result = null;
        StateChanged?.Invoke();
    }

    /// <summary>One-second tick from the panel's timer; on expiry advances (auto-submitting on the
    /// last question), so a timed exam cannot stall.</summary>
    public void Tick()
    {
        if (!IsTimed || Result is not null || Test is null || RemainingSeconds <= 0) return;
        RemainingSeconds--;
        if (RemainingSeconds <= 0) Next();
        else TimerTicked?.Invoke();
    }
}
