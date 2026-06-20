using System;
using System.Collections.Generic;
using System.Linq;
using CardioSimulator.Core.Data;
using CardioSimulator.Core.Domain;

namespace CardioSimulator.App.ViewModels;

/// <summary>
/// Authors a self-assessment <see cref="Test"/> — title, per-question time limit, and an ordered list
/// of single-choice questions (text, options, the correct option, an explanation comment, and the ECG
/// shown on the monitor) — then saves it via <see cref="TestRepository.WriteTest"/>. Modeled on the
/// course / OSCE constructors: a mutable edit model that compiles back to the immutable domain record
/// on save. The «по типу конструктора курсов» constructor Николай asked for.
/// </summary>
public sealed class TestConstructorViewModel
{
    private readonly TestRepository _repository;

    public TestConstructorViewModel(TestRepository repository)
    {
        _repository = repository;
    }

    public TestRepository Repository => _repository;

    public string? TestId { get; private set; }
    public string Title { get; set; } = string.Empty;
    public int QuestionTimeSeconds { get; set; }
    public List<EditQuestion> Questions { get; } = new();
    public bool IsDirty { get; set; }

    /// <summary>True once a test (new or loaded) is open for editing.</summary>
    public bool HasTest => TestId is not null;

    private static string NewId() => Guid.NewGuid().ToString("N")[..8];

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
        {
            Questions.Add(new EditQuestion
            {
                Id = q.Id,
                Text = q.Text,
                CorrectOptionId = q.CorrectOptionId,
                Comment = q.Comment,
                PathologyId = q.PathologyId,
                Options = q.Options.Select(o => new EditOption { Id = o.Id, Text = o.Text }).ToList(),
            });
        }
        IsDirty = false;
        return true;
    }

    public EditQuestion AddQuestion()
    {
        var q = new EditQuestion { Id = NewId() };
        for (var i = 0; i < 4; i++) q.Options.Add(new EditOption { Id = NewId() });
        if (q.Options.Count > 0) q.CorrectOptionId = q.Options[0].Id;
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
        var o = new EditOption { Id = NewId() };
        question.Options.Add(o);
        if (string.IsNullOrEmpty(question.CorrectOptionId)) question.CorrectOptionId = o.Id;
        IsDirty = true;
        return o;
    }

    public void RemoveOption(EditQuestion question, EditOption option)
    {
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
        {
            var q = Questions[i];
            var options = q.Options.Select(o => new TestOption(o.Id, o.Text.Trim())).ToList();
            var correct = !string.IsNullOrEmpty(q.CorrectOptionId)
                ? q.CorrectOptionId
                : (options.Count > 0 ? options[0].Id : string.Empty);
            questions.Add(new TestQuestion(
                q.Id,
                i + 1,
                q.Text.Trim(),
                options,
                correct,
                q.Comment.Trim(),
                string.IsNullOrWhiteSpace(q.PathologyId) ? null : q.PathologyId));
        }

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

    public sealed class EditQuestion
    {
        public string Id = string.Empty;
        public string Text = string.Empty;
        public List<EditOption> Options = new();
        public string CorrectOptionId = string.Empty;
        public string Comment = string.Empty;
        public string? PathologyId;
    }

    public sealed class EditOption
    {
        public string Id = string.Empty;
        public string Text = string.Empty;
    }
}
