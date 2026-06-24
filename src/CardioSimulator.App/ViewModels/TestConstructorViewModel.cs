using System;
using System.Collections.Generic;
using System.Linq;
using CardioSimulator.Core.Data;
using CardioSimulator.Core.Domain;

namespace CardioSimulator.App.ViewModels;

/// <summary>
/// Authors self-assessment <see cref="Test"/>s and curates the standing question bank
/// (<see cref="QuestionBankRepository"/>). A test is a title, a per-question time limit, and an ordered
/// list of single-choice questions; the bank is the reusable pool those questions are drawn from (and
/// the JSON import/export target). Both edit the same mutable <see cref="EditQuestion"/> model, which
/// compiles back to the immutable <see cref="TestQuestion"/> on save. Modeled on the course / OSCE
/// constructors. Tests <em>snapshot</em> bank questions (a fresh id per copy) — there is no live link.
/// </summary>
public sealed class TestConstructorViewModel
{
    /// <summary>Single-choice questions carry 4–6 options (the customer's spec); enforced by the UI.</summary>
    public const int MinOptions = 2;
    public const int MaxOptions = 6;

    private readonly TestRepository _repository;

    public TestConstructorViewModel(TestRepository repository, QuestionBankRepository bank, TestThemeStore themes)
    {
        _repository = repository;
        Bank = bank;
        Themes = themes;
    }

    public TestRepository Repository => _repository;
    public QuestionBankRepository Bank { get; }
    public TestThemeStore Themes { get; }

    // ── Test editing ────────────────────────────────────────────────────────

    public string? TestId { get; private set; }
    public string Title { get; set; } = string.Empty;
    public int QuestionTimeSeconds { get; set; }
    public List<EditQuestion> Questions { get; } = new();
    public bool IsDirty { get; set; }

    /// <summary>True once a test (new or loaded) is open for editing.</summary>
    public bool HasTest => TestId is not null;

    public static string NewId() => Guid.NewGuid().ToString("N")[..8];

    /// <summary>Starts a fresh test with one empty 4-option question.</summary>
    public void NewTest()
    {
        TestId = NewId();
        Title = string.Empty;
        QuestionTimeSeconds = 300;
        Questions.Clear();
        AddQuestion();
        IsDirty = false;
    }

    /// <summary>Loads an existing test into the edit model. Returns false if it is missing.</summary>
    public bool Load(string testId)
    {
        if (_repository.Test(testId) is not { } test) return false;
        TestId = test.TestId;
        Title = test.Title;
        QuestionTimeSeconds = test.QuestionTimeSeconds;
        Questions.Clear();
        foreach (var q in test.Questions)
            Questions.Add(EditQuestion.From(q));
        IsDirty = false;
        return true;
    }

    public EditQuestion AddQuestion()
    {
        var q = NewEditQuestion();
        Questions.Add(q);
        IsDirty = true;
        return q;
    }

    /// <summary>Snapshots a bank question into the current test as a new question (fresh id).</summary>
    public EditQuestion AddFromBank(TestQuestion bankQuestion)
    {
        var q = EditQuestion.From(bankQuestion);
        q.Id = NewId(); // a test question is a copy, not a live reference
        Questions.Add(q);
        IsDirty = true;
        return q;
    }

    public void RemoveQuestion(EditQuestion question)
    {
        Questions.Remove(question);
        IsDirty = true;
    }

    public EditOption AddOption(EditQuestion question)
    {
        if (question.Options.Count >= MaxOptions) return question.Options[^1];
        var o = new EditOption { Id = NewId() };
        question.Options.Add(o);
        if (string.IsNullOrEmpty(question.CorrectOptionId)) question.CorrectOptionId = o.Id;
        IsDirty = true;
        return o;
    }

    public void RemoveOption(EditQuestion question, EditOption option)
    {
        if (question.Options.Count <= MinOptions) return;
        question.Options.Remove(option);
        if (question.CorrectOptionId == option.Id)
            question.CorrectOptionId = question.Options.Count > 0 ? question.Options[0].Id : string.Empty;
        IsDirty = true;
    }

    /// <summary>Compiles the edit model and persists it. Returns false if nothing is open.</summary>
    public bool Save()
    {
        if (TestId is null) return false;
        var questions = new List<TestQuestion>();
        for (var i = 0; i < Questions.Count; i++)
            questions.Add(Questions[i].Compile(i + 1));

        var test = new Test(TestId, string.IsNullOrWhiteSpace(Title) ? TestId : Title.Trim(), questions, QuestionTimeSeconds);
        var ok = _repository.WriteTest(test);
        if (ok) IsDirty = false;
        return ok;
    }

    /// <summary>Deletes the open test from disk and clears the edit model.</summary>
    public bool Delete()
    {
        if (TestId is null) return false;
        var ok = _repository.DeleteTest(TestId);
        if (ok)
        {
            TestId = null;
            Title = string.Empty;
            Questions.Clear();
            IsDirty = false;
        }
        return ok;
    }

    // ── Bank editing ──────────────────────────────────────────────────────--

    /// <summary>The bank question currently open in the card editor (null = bank list view).</summary>
    public EditQuestion? BankEdit { get; private set; }

    public void NewBankQuestion() => BankEdit = NewEditQuestion();

    public bool EditBankQuestion(string id)
    {
        if (Bank.Question(id) is not { } q) return false;
        BankEdit = EditQuestion.From(q);
        return true;
    }

    public void CancelBankEdit() => BankEdit = null;

    /// <summary>Persists the open bank question (number is irrelevant in the bank → 0). Returns false
    /// if nothing is open or the write fails.</summary>
    public bool SaveBankQuestion()
    {
        if (BankEdit is null) return false;
        var ok = Bank.WriteQuestion(BankEdit.Compile(0));
        if (ok) BankEdit = null;
        return ok;
    }

    public bool DeleteBankQuestion(string id) => Bank.DeleteQuestion(id);

    /// <summary>Saves an in-test question into the bank (a copy keeping its id, number reset).</summary>
    public bool SaveQuestionToBank(EditQuestion question) => Bank.WriteQuestion(question.Compile(0));

    private static EditQuestion NewEditQuestion()
    {
        var q = new EditQuestion { Id = NewId() };
        for (var i = 0; i < 4; i++) q.Options.Add(new EditOption { Id = NewId() });
        q.CorrectOptionId = q.Options[0].Id;
        return q;
    }

    public sealed class EditQuestion
    {
        public string Id = string.Empty;
        public string Text = string.Empty;
        public List<EditOption> Options = new();
        public string CorrectOptionId = string.Empty;
        public string Comment = string.Empty;
        public string? PathologyId;
        public string? ImagePath;
        public string? Theme;
        public List<string> Tags = new();

        /// <summary>The chosen stimulus type — explicit (not derived), so the editor can show the ECG
        /// picker / image button before any specific ECG or picture is selected.</summary>
        public QuestionStimulus Kind = QuestionStimulus.Text;

        /// <summary>Alias for <see cref="Kind"/> (parallels <see cref="TestQuestion.Stimulus"/>).</summary>
        public QuestionStimulus Stimulus() => Kind;

        public static EditQuestion From(TestQuestion q) => new()
        {
            Id = q.Id,
            Text = q.Text,
            CorrectOptionId = q.CorrectOptionId,
            Comment = q.Comment,
            PathologyId = q.PathologyId,
            ImagePath = q.ImagePath,
            Theme = q.Theme,
            Tags = q.TagList.ToList(),
            Kind = q.Stimulus,
            Options = q.Options.Select(o => new EditOption { Id = o.Id, Text = o.Text }).ToList(),
        };

        public TestQuestion Compile(int number)
        {
            var options = Options.Select(o => new TestOption(o.Id, o.Text.Trim())).ToList();
            var correct = !string.IsNullOrEmpty(CorrectOptionId)
                ? CorrectOptionId
                : (options.Count > 0 ? options[0].Id : string.Empty);
            var tags = Tags.Select(t => t.Trim()).Where(t => t.Length > 0).ToList();
            // Only the chosen stimulus's content is persisted; the other is dropped.
            var pathologyId = Kind == QuestionStimulus.Ecg && !string.IsNullOrWhiteSpace(PathologyId) ? PathologyId : null;
            var imagePath = Kind == QuestionStimulus.Image && !string.IsNullOrWhiteSpace(ImagePath) ? ImagePath : null;
            return new TestQuestion(
                Id,
                number,
                Text.Trim(),
                options,
                correct,
                Comment.Trim(),
                PathologyId: pathologyId,
                ImagePath: imagePath,
                Theme: string.IsNullOrWhiteSpace(Theme) ? null : Theme!.Trim(),
                Tags: tags.Count > 0 ? tags : null);
        }
    }

    public sealed class EditOption
    {
        public string Id = string.Empty;
        public string Text = string.Empty;
    }
}
