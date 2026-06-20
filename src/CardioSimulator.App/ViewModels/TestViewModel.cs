using System;
using CardioSimulator.Core.Domain;

namespace CardioSimulator.App.ViewModels;

/// <summary>
/// Drives one self-assessment attempt: the active <see cref="Test"/>, the current question index, the
/// student's pick + immediate grading, the per-question countdown, and the running score. Plain state
/// machine — the <see cref="Controls.TestQuestionPanel"/> renders it and the <see cref="Screens.TestingScreen"/>
/// listens to drive the monitor. Two events keep the once-a-second timer cheap: <see cref="StateChanged"/>
/// fires on structural changes (start/answer/next/finish) for a full re-render, <see cref="TimerTicked"/>
/// only when the remaining seconds change, so the panel updates a single label.
/// </summary>
public sealed class TestViewModel
{
    public Test? Test { get; private set; }
    public int Index { get; private set; }
    public bool Revealed { get; private set; }
    public bool Finished { get; private set; }
    public string? SelectedOptionId { get; private set; }
    public int CorrectCount { get; private set; }
    public int RemainingSeconds { get; private set; }

    /// <summary>Fired on start/answer/next/finish/close — a full re-render is needed.</summary>
    public event Action? StateChanged;

    /// <summary>Fired on each countdown decrement — only the timer label needs updating.</summary>
    public event Action? TimerTicked;

    public int Count => Test?.Questions.Count ?? 0;

    public TestQuestion? Current =>
        Test is not null && Index >= 0 && Index < Test.Questions.Count ? Test.Questions[Index] : null;

    /// <summary>True while a test is being taken (started and not yet finished).</summary>
    public bool HasActiveTest => Test is not null && !Finished;

    public bool IsTimed => (Test?.QuestionTimeSeconds ?? 0) > 0;

    /// <summary>True on the last question (so the panel labels the button «Завершить»).</summary>
    public bool IsLastQuestion => Test is not null && Index + 1 >= Test.Questions.Count;

    /// <summary>True once revealed when the student picked the key (a wrong/timed-out answer is false).</summary>
    public bool AnswerCorrect =>
        Revealed && SelectedOptionId is not null && SelectedOptionId == Current?.CorrectOptionId;

    public void Start(Test test)
    {
        Test = test;
        Index = 0;
        CorrectCount = 0;
        Finished = false;
        BeginQuestion();
    }

    public void Restart()
    {
        if (Test is { } t) Start(t);
    }

    /// <summary>Drops the active attempt back to the test picker.</summary>
    public void Close()
    {
        Test = null;
        Finished = false;
        Index = 0;
        Revealed = false;
        SelectedOptionId = null;
        StateChanged?.Invoke();
    }

    private void BeginQuestion()
    {
        Revealed = false;
        SelectedOptionId = null;
        RemainingSeconds = Test?.QuestionTimeSeconds ?? 0;
        StateChanged?.Invoke();
    }

    /// <summary>Records the student's single answer, grades it immediately, and reveals the comment.</summary>
    public void Select(string optionId)
    {
        if (Test is null || Revealed || Current is null) return;
        SelectedOptionId = optionId;
        Revealed = true;
        if (optionId == Current.CorrectOptionId) CorrectCount++;
        StateChanged?.Invoke();
    }

    /// <summary>Reveals the answer with no selection (the countdown ran out) — graded as incorrect.</summary>
    public void RevealUnanswered()
    {
        if (Test is null || Revealed) return;
        Revealed = true;
        StateChanged?.Invoke();
    }

    public void Next()
    {
        if (Test is null) return;
        if (IsLastQuestion)
        {
            Finished = true;
            StateChanged?.Invoke();
            return;
        }
        Index++;
        BeginQuestion();
    }

    /// <summary>One-second tick from the panel's timer; decrements and auto-reveals at zero.</summary>
    public void Tick()
    {
        if (!IsTimed || Revealed || Finished || Test is null || RemainingSeconds <= 0) return;
        RemainingSeconds--;
        if (RemainingSeconds <= 0) RevealUnanswered();
        else TimerTicked?.Invoke();
    }
}
