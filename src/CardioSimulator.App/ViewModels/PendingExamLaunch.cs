using CardioSimulator.App.Data;
using CardioSimulator.Core.Domain;

namespace CardioSimulator.App.ViewModels;

/// <summary>
/// A graded exam launch queued from the Learning Scale «Сдать» button (A3, customer 28-08-2026): the id of
/// the block's key test, the <see cref="ExamStudentInfo"/> to record the attempt for, and the roster
/// <see cref="Student"/> to re-select on the dashboard when the exam is finished. Set on
/// <see cref="AppViewModel.PendingExamLaunch"/>; consumed once by the Examination screen.
/// </summary>
public sealed record PendingExamLaunch(string TestId, ExamStudentInfo Student, Student? ReturnStudent);
