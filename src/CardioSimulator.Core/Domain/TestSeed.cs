using System.Collections.Generic;
using System.Linq;

namespace CardioSimulator.Core.Domain;

/// <summary>
/// Built-in sample self-assessment test, seeded on first run so the Testing screen has content out of
/// the box (mirroring <see cref="OskeSeedForms"/>). The first question reproduces the UI prototype.
/// Each question is bound to one of the dataset's pathologies (<paramref name="pathologyIds"/>) so the
/// monitor shows a real trace; when fewer ECGs are available the link is simply left empty.
/// </summary>
public static class TestSeed
{
    public const string SampleTestId = "sample";

    /// <summary>5-minute per-question countdown, matching the prototype's «4:59».</summary>
    private const int SampleQuestionSeconds = 300;

    public static Test Sample(IReadOnlyList<string> pathologyIds)
    {
        string? Ecg(int i) => i < pathologyIds.Count ? pathologyIds[i] : null;

        var questions = new List<TestQuestion>
        {
            new(
                Id: "q1",
                Number: 1,
                Text: "Найти депрессию (если она имеется), проверить, является ли она вторичной; " +
                      "если нет — отнести её в разряд «патологической, требующей дифференциальной " +
                      "диагностики (ДД)».",
                Options: new List<TestOption>
                {
                    new("a", "Депрессия присутствует"),
                    new("b", "Отсутствует, так как…"),
                    new("c", "Да, вторичная"),
                    new("d", "ПДД"),
                },
                CorrectOptionId: "a",
                Comment: "На графике видно, что в сегменте AVL и V5, V7 чётко видны подъёмы сегмента ST.",
                PathologyId: Ecg(0)),

            new(
                Id: "q2",
                Number: 2,
                Text: "Определите основной ритм на представленной электрокардиограмме.",
                Options: new List<TestOption>
                {
                    new("a", "Синусовый ритм"),
                    new("b", "Фибрилляция предсердий"),
                    new("c", "Желудочковая тахикардия"),
                    new("d", "АВ-блокада III степени"),
                },
                CorrectOptionId: "a",
                Comment: "Зубец P предшествует каждому комплексу QRS, интервалы R–R регулярны — " +
                         "это синусовый ритм.",
                PathologyId: Ecg(1)),

            new(
                Id: "q3",
                Number: 3,
                Text: "Оцените частоту сердечных сокращений по интервалу R–R.",
                Options: new List<TestOption>
                {
                    new("a", "Менее 60 в минуту (брадикардия)"),
                    new("b", "60–90 в минуту (нормосистолия)"),
                    new("c", "Более 100 в минуту (тахикардия)"),
                    new("d", "Определить невозможно"),
                },
                CorrectOptionId: "b",
                Comment: "Интервал R–R соответствует частоте около 75 ударов в минуту — нормосистолия.",
                PathologyId: Ecg(2)),
        };

        return new Test(SampleTestId, "Демонстрационный тест", questions, SampleQuestionSeconds);
    }
}
