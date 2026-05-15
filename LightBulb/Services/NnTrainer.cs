using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace LightBulb.Services;

/// <summary>
/// Trains an 11→16(tanh)→4(sigmoid×100) neural network using Adam optimizer.
/// Weight layout matches NnModelEvaluator and nn-engine.js exactly:
///   W1[j][i]: input i → hidden j,  shape [16][11]
///   W2[k][j]: hidden j → output k, shape [4][16]
/// </summary>
public sealed class NnTrainer
{
    private const int In  = 11;
    private const int Hid = 16;
    private const int Out = 4;

    private const double B1  = 0.9;
    private const double B2  = 0.999;
    private const double Eps = 1e-8;

    private double[][] _w1, _w2;
    private double[]   _b1, _b2;

    // Adam first/second moments
    private double[][] _mW1, _vW1, _mW2, _vW2;
    private double[]   _mB1, _vB1, _mB2, _vB2;

    private int _step;

    public NnTrainer()
    {
        var rng = new Random(42);
        _w1  = Xavier(rng, Hid, In);
        _b1  = new double[Hid];
        _w2  = Xavier(rng, Out, Hid);
        _b2  = new double[Out];
        _mW1 = ZeroMat(Hid, In);  _vW1 = ZeroMat(Hid, In);
        _mW2 = ZeroMat(Out, Hid); _vW2 = ZeroMat(Out, Hid);
        _mB1 = new double[Hid];   _vB1 = new double[Hid];
        _mB2 = new double[Out];   _vB2 = new double[Out];
    }

    /// <summary>
    /// Trains for <paramref name="epochs"/> epochs on <paramref name="samples"/>.
    /// Returns the final mean-squared error (output scale 0-100).
    /// </summary>
    public double Train(
        IReadOnlyList<(double[] inputs, double[] targets)> samples,
        int epochs, double lr, double lambda = 1e-4)
    {
        if (samples.Count == 0) return 0;
        double mse = 0;

        for (int ep = 0; ep < epochs; ep++)
        {
            _step++;
            var gW1 = ZeroMat(Hid, In);
            var gB1 = new double[Hid];
            var gW2 = ZeroMat(Out, Hid);
            var gB2 = new double[Out];
            mse = 0;

            foreach (var (x, t) in samples)
            {
                // Forward
                var h   = new double[Hid];
                var sig = new double[Out];
                var yh  = new double[Out];

                for (int j = 0; j < Hid; j++)
                {
                    double s = _b1[j];
                    for (int i = 0; i < In; i++) s += _w1[j][i] * x[i];
                    h[j] = Math.Tanh(s);
                }
                for (int k = 0; k < Out; k++)
                {
                    double s = _b2[k];
                    for (int j = 0; j < Hid; j++) s += _w2[k][j] * h[j];
                    sig[k] = 1.0 / (1.0 + Math.Exp(-s));
                    yh[k]  = sig[k] * 100.0;
                    double d = yh[k] - t[k];
                    mse += d * d;
                }

                // Backward — dL/dz2
                var dz2 = new double[Out];
                for (int k = 0; k < Out; k++)
                {
                    double d = yh[k] - t[k];
                    // MSE gradient × chain through sigmoid × 100 scale
                    dz2[k] = (2.0 / samples.Count) * d * 100.0 * sig[k] * (1.0 - sig[k]);
                }

                for (int k = 0; k < Out; k++)
                {
                    gB2[k] += dz2[k];
                    for (int j = 0; j < Hid; j++)
                        gW2[k][j] += dz2[k] * h[j];
                }

                var dh  = new double[Hid];
                var dz1 = new double[Hid];
                for (int j = 0; j < Hid; j++)
                    for (int k = 0; k < Out; k++)
                        dh[j] += _w2[k][j] * dz2[k];

                for (int j = 0; j < Hid; j++)
                {
                    dz1[j] = dh[j] * (1.0 - h[j] * h[j]);
                    gB1[j] += dz1[j];
                    for (int i = 0; i < In; i++)
                        gW1[j][i] += dz1[j] * x[i];
                }
            }

            mse /= samples.Count * Out;

            double bc1 = 1.0 - Math.Pow(B1, _step);
            double bc2 = 1.0 - Math.Pow(B2, _step);
            AdamMat(_w1, gW1, _mW1, _vW1, lr, lambda, bc1, bc2);
            AdamVec(_b1, gB1, _mB1, _vB1, lr, bc1, bc2);
            AdamMat(_w2, gW2, _mW2, _vW2, lr, lambda, bc1, bc2);
            AdamVec(_b2, gB2, _mB2, _vB2, lr, bc1, bc2);
        }

        return mse;
    }

    /// <summary>Serialises weights to JSON matching the format expected by NnModelEvaluator.</summary>
    public string ExportJson()
    {
        using var ms = new MemoryStream();
        using var w  = new Utf8JsonWriter(ms);
        w.WriteStartObject();
        WriteMatrix(w, "W1", _w1);
        WriteVec(w,    "b1", _b1);
        WriteMatrix(w, "W2", _w2);
        WriteVec(w,    "b2", _b2);
        w.WriteEndObject();
        w.Flush();
        return System.Text.Encoding.UTF8.GetString(ms.ToArray());
    }

    /// <summary>Restores weights from previously exported JSON so training can resume.</summary>
    public static NnTrainer? FromJson(string json)
    {
        try
        {
            var doc  = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var t    = new NnTrainer();
            t._w1 = ReadMatrix(root.GetProperty("W1"), Hid, In);
            t._b1 = ReadVec(root.GetProperty("b1"), Hid);
            t._w2 = ReadMatrix(root.GetProperty("W2"), Out, Hid);
            t._b2 = ReadVec(root.GetProperty("b2"), Out);
            return t;
        }
        catch { return null; }
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static double[][] Xavier(Random rng, int rows, int cols)
    {
        double scale = Math.Sqrt(2.0 / cols);
        var m = new double[rows][];
        for (int r = 0; r < rows; r++)
        {
            m[r] = new double[cols];
            for (int c = 0; c < cols; c++)
                m[r][c] = (rng.NextDouble() * 2 - 1) * scale;
        }
        return m;
    }

    private static double[][] ZeroMat(int rows, int cols)
    {
        var m = new double[rows][];
        for (int r = 0; r < rows; r++) m[r] = new double[cols];
        return m;
    }

    private static void AdamMat(
        double[][] w, double[][] g,
        double[][] m, double[][] v,
        double lr, double lambda, double bc1, double bc2)
    {
        for (int r = 0; r < w.Length; r++)
            for (int c = 0; c < w[r].Length; c++)
            {
                double grad = g[r][c] + lambda * w[r][c];
                m[r][c] = B1 * m[r][c] + (1 - B1) * grad;
                v[r][c] = B2 * v[r][c] + (1 - B2) * grad * grad;
                w[r][c] -= lr * (m[r][c] / bc1) / (Math.Sqrt(v[r][c] / bc2) + Eps);
            }
    }

    private static void AdamVec(
        double[] w, double[] g,
        double[] m, double[] v,
        double lr, double bc1, double bc2)
    {
        for (int i = 0; i < w.Length; i++)
        {
            m[i] = B1 * m[i] + (1 - B1) * g[i];
            v[i] = B2 * v[i] + (1 - B2) * g[i] * g[i];
            w[i] -= lr * (m[i] / bc1) / (Math.Sqrt(v[i] / bc2) + Eps);
        }
    }

    private static void WriteMatrix(Utf8JsonWriter w, string name, double[][] m)
    {
        w.WritePropertyName(name);
        w.WriteStartArray();
        foreach (var row in m)
        {
            w.WriteStartArray();
            foreach (var v in row) w.WriteNumberValue(v);
            w.WriteEndArray();
        }
        w.WriteEndArray();
    }

    private static void WriteVec(Utf8JsonWriter w, string name, double[] v)
    {
        w.WritePropertyName(name);
        w.WriteStartArray();
        foreach (var x in v) w.WriteNumberValue(x);
        w.WriteEndArray();
    }

    private static double[][] ReadMatrix(JsonElement el, int rows, int cols)
    {
        var m = new double[rows][];
        for (int r = 0; r < rows; r++)
        {
            m[r] = new double[cols];
            for (int c = 0; c < cols; c++)
                m[r][c] = el[r][c].GetDouble();
        }
        return m;
    }

    private static double[] ReadVec(JsonElement el, int n)
    {
        var v = new double[n];
        for (int i = 0; i < n; i++) v[i] = el[i].GetDouble();
        return v;
    }
}
