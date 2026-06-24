using System.Collections.Generic;
using CardioSimulator.Core.Domain;

namespace CardioSimulator.Core.Data;

/// <summary>
/// Read access to the standing question bank — the pool of authored / AI-generated questions that
/// tests are assembled from. Mirrors <see cref="ITestSource"/>; writes live on the concrete
/// <see cref="FileQuestionBankSource"/>.
/// </summary>
public interface IQuestionBankSource
{
    IReadOnlyList<TestQuestion> ReadQuestions();
    TestQuestion? ReadQuestion(string id);
    bool IsValid();
}
