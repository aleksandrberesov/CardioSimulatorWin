using System;
using System.Collections.Generic;
using CardioSimulator.Core.Domain;
using Xunit;

namespace CardioSimulator.Core.Tests;

public class TipOverlaySerializerTests
{
    private static readonly List<TipOverlay> Sample = new()
    {
        new TipOverlay(TipOverlayKind.VerticalLines, new[] { new TipPoint(100, 0), new TipPoint(250, 0) }),
        new TipOverlay(TipOverlayKind.HorizontalLines, new[] { new TipPoint(0, 500) }),
        new TipOverlay(TipOverlayKind.Label, new[] { new TipPoint(120, 200) }, Text: "P wave | note ~ here"),
        new TipOverlay(TipOverlayKind.Points, new[] { new TipPoint(300, 100), new TipPoint(400, -50) }),
    };

    [Fact]
    public void Serialize_Parse_RoundTripsAllKindsAndEscaping()
    {
        var round = TipOverlaySerializer.Parse(TipOverlaySerializer.Serialize(Sample));
        Assert.Equal(4, round.Count);
        Assert.Equal(TipOverlayKind.VerticalLines, round[0].Kind);
        Assert.Equal(2, round[0].Points.Count);
        Assert.Equal(100, round[0].Points[0].Sample);
        Assert.Equal(500, round[1].Points[0].Adc);
        Assert.Equal("P wave | note ~ here", round[2].Text); // '|' and '~' survive escaping
        Assert.Equal(TipOverlayKind.Points, round[3].Kind);
        Assert.Equal(-50, round[3].Points[1].Adc);
    }

    [Fact]
    public void EncodeAttribute_IsHtmlAttributeSafe_AndRoundTrips()
    {
        var enc = TipOverlaySerializer.EncodeAttribute(Sample);
        Assert.DoesNotContain("\"", enc);
        Assert.DoesNotContain("<", enc);
        Assert.DoesNotContain(">", enc);

        var dec = TipOverlaySerializer.DecodeAttribute(enc);
        Assert.Equal(4, dec.Count);
        Assert.Equal("P wave | note ~ here", dec[2].Text);
    }

    [Fact]
    public void EncodeAttribute_Empty_IsEmpty_AndDecodeTolerant()
    {
        Assert.Equal(string.Empty, TipOverlaySerializer.EncodeAttribute(Array.Empty<TipOverlay>()));
        Assert.Empty(TipOverlaySerializer.DecodeAttribute(null));
        Assert.Empty(TipOverlaySerializer.DecodeAttribute("not-valid-base64!!"));
    }
}
