using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using BioSPPy.Net.Signals.Ecg;
using BioSPPy.Net.Signals.Tools;
using BioSPPy.Net.Synthesizers.Ecg;
using Xunit;

namespace CardioSimulator.Core.Tests;

public class TestData
{
    public double[] raw { get; set; }
    public double[] filtered { get; set; }
    public int[] rpeaks_hamilton { get; set; }
    public int[] rpeaks_ssf { get; set; }
    public int[] rpeaks_christov { get; set; }
    public int[] rpeaks_engzee { get; set; }
    public int[] rpeaks_gamboa { get; set; }
    public int[] rpeaks_asi { get; set; }
    
    public double[] pipeline_filtered { get; set; }
    public int[] pipeline_rpeaks { get; set; }
    public double[] pipeline_ts { get; set; }
    public double[] pipeline_templates_ts { get; set; }
    public double[][] pipeline_templates { get; set; }
    public double[] pipeline_heart_rate_ts { get; set; }
    public double[] pipeline_heart_rate { get; set; }
}

public class BioSPPyNetTests
{
    private readonly TestData _data;

    public BioSPPyNetTests()
    {
        string jsonPath = @"C:\Users\Aleksandr\.gemini\antigravity-ide\brain\23618329-c49a-4400-9013-a97536a286ee\scratch\test_ecg_data.json";
        if (File.Exists(jsonPath))
        {
            string jsonText = File.ReadAllText(jsonPath);
            _data = JsonSerializer.Deserialize<TestData>(jsonText);
        }
    }

    [Fact]
    public void TestFIRFilter()
    {
        Assert.NotNull(_data);
        var res = Filtering.FilterSignal(
            _data.raw,
            ftype: "FIR",
            band: "bandpass",
            order: (int)(0.3 * 1000.0) + 1, // 301
            frequency: new double[] { 3.0, 45.0 },
            sampling_rate: 1000.0
        );

        Assert.Equal(_data.filtered.Length, res.signal.Length);
        for (int i = 0; i < res.signal.Length; i++)
        {
            Assert.True(Math.Abs(res.signal[i] - _data.filtered[i]) < 1e-5, $"Mismatch at {i}: C#={res.signal[i]}, Py={_data.filtered[i]}");
        }
    }

    [Fact]
    public void TestHamiltonSegmenter()
    {
        Assert.NotNull(_data);
        var rpeaks = QrsSegmenters.HamiltonSegmenter(_data.filtered, 1000.0);

        Assert.Equal(_data.rpeaks_hamilton.Length, rpeaks.Length);
        for (int i = 0; i < rpeaks.Length; i++)
        {
            Assert.Equal(_data.rpeaks_hamilton[i], rpeaks[i]);
        }
    }

    [Fact]
    public void TestSsfSegmenter()
    {
        Assert.NotNull(_data);
        var rpeaks = QrsSegmenters.SsfSegmenter(_data.filtered, 1000.0);

        Assert.Equal(_data.rpeaks_ssf.Length, rpeaks.Length);
        for (int i = 0; i < rpeaks.Length; i++)
        {
            Assert.Equal(_data.rpeaks_ssf[i], rpeaks[i]);
        }
    }

    [Fact]
    public void TestChristovSegmenter()
    {
        Assert.NotNull(_data);
        var rpeaks = QrsSegmenters.ChristovSegmenter(_data.filtered, 1000.0);

        Assert.Equal(_data.rpeaks_christov.Length, rpeaks.Length);
        for (int i = 0; i < rpeaks.Length; i++)
        {
            Assert.Equal(_data.rpeaks_christov[i], rpeaks[i]);
        }
    }

    [Fact]
    public void TestEngzeeSegmenter()
    {
        Assert.NotNull(_data);
        var rpeaks = QrsSegmenters.EngzeeSegmenter(_data.filtered, 1000.0);

        Assert.Equal(_data.rpeaks_engzee.Length, rpeaks.Length);
        for (int i = 0; i < rpeaks.Length; i++)
        {
            Assert.Equal(_data.rpeaks_engzee[i], rpeaks[i]);
        }
    }

    [Fact]
    public void TestGamboaSegmenter()
    {
        Assert.NotNull(_data);
        var rpeaks = QrsSegmenters.GamboaSegmenter(_data.filtered, 1000.0);

        Assert.Equal(_data.rpeaks_gamboa.Length, rpeaks.Length);
        for (int i = 0; i < rpeaks.Length; i++)
        {
            Assert.Equal(_data.rpeaks_gamboa[i], rpeaks[i]);
        }
    }

    [Fact]
    public void TestAsiSegmenter()
    {
        Assert.NotNull(_data);
        var rpeaks = QrsSegmenters.AsiSegmenter(_data.raw, 1000.0);

        Assert.Equal(_data.rpeaks_asi.Length, rpeaks.Length);
        for (int i = 0; i < rpeaks.Length; i++)
        {
            Assert.Equal(_data.rpeaks_asi[i], rpeaks[i]);
        }
    }

    [Fact]
    public void TestCompletePipeline()
    {
        Assert.NotNull(_data);
        var result = EcgProcess.Process(_data.raw, 1000.0);

        // Verify Filtered
        Assert.Equal(_data.pipeline_filtered.Length, result.Filtered.Length);
        for (int i = 0; i < result.Filtered.Length; i++)
        {
            Assert.True(Math.Abs(result.Filtered[i] - _data.pipeline_filtered[i]) < 1e-5, $"Filtered mismatch at {i}");
        }

        // Verify R-Peaks
        Assert.Equal(_data.pipeline_rpeaks.Length, result.RPeaks.Length);
        for (int i = 0; i < result.RPeaks.Length; i++)
        {
            Assert.Equal(_data.pipeline_rpeaks[i], result.RPeaks[i]);
        }

        // Verify Heart Rate
        Assert.Equal(_data.pipeline_heart_rate.Length, result.HeartRate.Length);
        for (int i = 0; i < result.HeartRate.Length; i++)
        {
            Assert.True(Math.Abs(result.HeartRate[i] - _data.pipeline_heart_rate[i]) < 1e-2, $"HeartRate mismatch at {i}: C#={result.HeartRate[i]}, Py={_data.pipeline_heart_rate[i]}");
        }

        // Verify Templates
        int nTemplates = result.Templates.GetLength(0);
        int templateSize = result.Templates.GetLength(1);
        Assert.Equal(_data.pipeline_templates.Length, nTemplates);
        for (int i = 0; i < nTemplates; i++)
        {
            Assert.Equal(_data.pipeline_templates[i].Length, templateSize);
            for (int j = 0; j < templateSize; j++)
            {
                Assert.True(Math.Abs(result.Templates[i, j] - _data.pipeline_templates[i][j]) < 1e-5, $"Template mismatch at [{i},{j}]: C#={result.Templates[i, j]}, Py={_data.pipeline_templates[i][j]}");
            }
        }
    }

    [Fact]
    public void TestSynthesizer()
    {
        var (ecg, t, parameters) = DolinskySynthesizer.Generate(var: 0.0);
        Assert.NotNull(ecg);
        Assert.NotEmpty(ecg);
        Assert.Equal(ecg.Length, t.Length);
        Assert.Equal(0.0, parameters["var"]);
    }
}
