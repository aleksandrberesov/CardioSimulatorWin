using CardioSimulator.Core.Domain;
using Xunit;

namespace CardioSimulator.Core.Tests;

public class EosAxisTests
{
    [Fact]
    public void NetMm_IsRMinusQAndS()
    {
        var m = new EosLeadMeasure(QMm: 1, RMm: 10, SMm: 3);
        Assert.Equal(6, m.NetMm, 3); // R - (q + S) = 10 - (1 + 3)
    }

    [Theory]
    [InlineData(1, 0, 0)]      // pure lead I positive → 0°
    [InlineData(0, 1, 90)]     // pure aVF positive → +90°
    [InlineData(1, 1, 45)]     // equal → +45°
    [InlineData(-1, 0, 180)]   // lead I negative → 180°
    [InlineData(0, -1, -90)]   // aVF negative → -90°
    public void AngleDegrees_MatchesHexaxialProjection(double netI, double netAvf, double expected)
    {
        Assert.Equal(expected, EosAxis.AngleDegrees(netI, netAvf), 3);
    }

    [Theory]
    [InlineData(0, EosAxisClass.Horizontal)]
    [InlineData(29, EosAxisClass.Horizontal)]
    [InlineData(30, EosAxisClass.Normal)]
    [InlineData(45, EosAxisClass.Normal)]
    [InlineData(69, EosAxisClass.Normal)]
    [InlineData(70, EosAxisClass.Vertical)]
    [InlineData(90, EosAxisClass.Vertical)]
    [InlineData(120, EosAxisClass.RightDeviation)]
    [InlineData(180, EosAxisClass.RightDeviation)]
    [InlineData(-1, EosAxisClass.LeftDeviation)]
    [InlineData(-89, EosAxisClass.LeftDeviation)]
    [InlineData(-90, EosAxisClass.ExtremeDeviation)]
    [InlineData(-135, EosAxisClass.ExtremeDeviation)]
    public void Classify_PartitionsTheCircleIntoCustomerBands(double angle, EosAxisClass expected)
    {
        Assert.Equal(expected, EosAxis.Classify(angle));
    }

    [Fact]
    public void From_WorkedExample_ComputesAngleAndBand()
    {
        // Customer's worked example: lead I net = 2 mm (rightward), aVF net = 6 mm (downward).
        // α = atan2(6, 2) ≈ 71.6°, which by the customer's own bands is Vertical (70–90°), even
        // though their illustrative slide loosely labels it "Normal".
        var leadI = new EosLeadMeasure(QMm: 5, RMm: 10, SMm: 3);   // net = 2
        var leadAvf = new EosLeadMeasure(QMm: 1, RMm: 10, SMm: 3); // net = 6

        var result = EosAxis.From(leadI, leadAvf);

        Assert.Equal(2, result.LeadI.NetMm, 3);
        Assert.Equal(6, result.LeadAvf.NetMm, 3);
        Assert.Equal(71.565, result.AngleDeg, 1);
        Assert.Equal(EosAxisClass.Vertical, result.AxisClass);
    }
}
