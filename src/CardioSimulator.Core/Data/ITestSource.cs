using System.Collections.Generic;
using CardioSimulator.Core.Domain;

namespace CardioSimulator.Core.Data;

/// <summary>
/// Read access to the self-assessment tests. Mirrors <see cref="IOskeSource"/> / <see cref="ICourseSource"/>;
/// writes live on the concrete <see cref="FileTestSource"/>.
/// </summary>
public interface ITestSource
{
    IReadOnlyList<Test> ReadTests();
    Test? ReadTest(string testId);
    bool IsValid();
}
