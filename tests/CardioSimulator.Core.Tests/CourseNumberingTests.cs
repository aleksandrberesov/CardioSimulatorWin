using CardioSimulator.Core.Domain;
using Xunit;

namespace CardioSimulator.Core.Tests;

public class CourseNumberingTests
{
    [Theory]
    // Real titles from the customer's course pack (numbering lives in the title, no subsection: field).
    [InlineData("Раздел 1. Теоретические основы и техника регистрации ЭКГ", "1")]
    [InlineData("2.1. Зубец Р", "2.1")]
    [InlineData("4.6. Атриовентрикулярные блокады (I, II, III степени)", "4.6")]
    [InlineData("6.13. Чреспищеводная электрическая стимуляция сердца (ЧПЭС)", "6.13")]
    [InlineData("6.10. Проба с дозированной физической нагрузкой (велоэргометрия)", "6.10")]
    [InlineData("Раздел 9. Особые группы пациентов и дифференциальная диагностика", "9")]
    public void NumberPrefix_ExtractsLeadingNumbering(string title, string expected) =>
        Assert.Equal(expected, CourseNumbering.NumberPrefix(title));

    [Theory]
    // Overview/section pages and prose carry no numbering → null (never mis-keyed).
    [InlineData("Нарушения ритма сердца.")]
    [InlineData("Анализ нормальной ЭКГ")]
    [InlineData("12 отведений ЭКГ")] // incidental leading number, not a section index
    [InlineData("")]
    [InlineData(null)]
    public void NumberPrefix_ReturnsNull_WhenNoNumbering(string? title) =>
        Assert.Null(CourseNumbering.NumberPrefix(title));

    [Fact]
    public void NumberPrefix_TrimsToSubtopicKey_ViaTaxonomy()
    {
        // The derived string feeds SubtopicKeyOf, exactly as the Learning Scale / picker use it.
        Assert.Equal("2.1", Taxonomy.SubtopicKeyOf(CourseNumbering.NumberPrefix("2.1. Зубец Р")!));
        Assert.Equal("1", Taxonomy.SubtopicKeyOf(CourseNumbering.NumberPrefix("Раздел 1. Теория")!));
    }
}
