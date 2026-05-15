using System;
using System.IO;
using System.Text.Json;

namespace LightBulb.Services;

public sealed class NnModelEvaluator
{
    // W1[j][i]: weight from input i to hidden j  → shape [16][11]
    // W2[k][j]: weight from hidden j to output k → shape [4][16]
    private readonly double[][] _w1;
    private readonly double[]   _b1;
    private readonly double[][] _w2;
    private readonly double[]   _b2;

    private NnModelEvaluator(double[][] w1, double[] b1, double[][] w2, double[] b2)
    {
        _w1 = w1; _b1 = b1; _w2 = w2; _b2 = b2;
    }

    public static NnModelEvaluator? Load(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            using var fs  = File.OpenRead(path);
            var root = JsonDocument.Parse(fs).RootElement;
            var w1 = ReadMatrix(root.GetProperty("W1"));
            var b1 = ReadVector(root.GetProperty("b1"));
            var w2 = ReadMatrix(root.GetProperty("W2"));
            var b2 = ReadVector(root.GetProperty("b2"));
            if (w1.Length != 16 || w2.Length != 4) return null;
            return new NnModelEvaluator(w1, b1, w2, b2);
        }
        catch { return null; }
    }

    /// <summary>
    /// Forward pass mirroring nn-engine.js: 11 → 16 (tanh) → 4 (sigmoid × 100).
    /// inputs must follow the same order as encodeInputs() in nn-engine.js.
    /// Returns (R, G, B, L) each in 0-100.
    /// </summary>
    public (double R, double G, double B, double L) Predict(double[] inputs)
    {
        Span<double> h = stackalloc double[16];
        for (int j = 0; j < 16; j++)
        {
            double s = _b1[j];
            for (int i = 0; i < 11; i++) s += _w1[j][i] * inputs[i];
            h[j] = Math.Tanh(s);
        }

        Span<double> o = stackalloc double[4];
        for (int k = 0; k < 4; k++)
        {
            double s = _b2[k];
            for (int j = 0; j < 16; j++) s += _w2[k][j] * h[j];
            o[k] = 1.0 / (1.0 + Math.Exp(-s)) * 100.0;
        }

        return (o[0], o[1], o[2], o[3]);
    }

    private static double[][] ReadMatrix(JsonElement el)
    {
        int rows = el.GetArrayLength();
        var m = new double[rows][];
        for (int i = 0; i < rows; i++)
        {
            var row = el[i];
            int cols = row.GetArrayLength();
            m[i] = new double[cols];
            for (int j = 0; j < cols; j++) m[i][j] = row[j].GetDouble();
        }
        return m;
    }

    private static double[] ReadVector(JsonElement el)
    {
        int n = el.GetArrayLength();
        var v = new double[n];
        for (int i = 0; i < n; i++) v[i] = el[i].GetDouble();
        return v;
    }
}
