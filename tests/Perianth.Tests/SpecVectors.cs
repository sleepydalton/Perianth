using System;
using System.IO;
using System.Text.Json;

namespace Perianth.Tests;

/// <summary>
/// Reads the golden vectors transcribed from porting specification section 7.6.
/// </summary>
/// <remarks>
/// The vectors are exposed as <see cref="JsonElement"/> rather than as typed
/// records because almost none of them have a decoder to feed yet. A dozen DTO
/// types modelling data nothing reads would be the wrong shape; each group earns
/// its own typed reader when the code that consumes it exists.
/// </remarks>
internal static class SpecVectors
{
    private static readonly JsonDocument Document = Load();

    /// <summary>Every group, keyed by identifier.</summary>
    public static JsonElement Groups => Document.RootElement.GetProperty("groups");

    /// <summary>One group by identifier.</summary>
    public static JsonElement Group(string id) => Groups.GetProperty(id);

    /// <summary>The comparison tolerance section 7.6 states for its decimal approximations.</summary>
    public static double Tolerance => Document.RootElement.GetProperty("float_tolerance").GetDouble();

    /// <summary>Reads a <c>bytes</c>-style hex string.</summary>
    public static byte[] Hex(JsonElement element, string property) =>
        Convert.FromHexString(element.GetProperty(property).GetString()
            ?? throw new InvalidOperationException("A hex field was null."));

    /// <summary>Reads a rectangular grid of integers given as an array of rows.</summary>
    public static int[][] Grid(JsonElement element, string property)
    {
        JsonElement rows = element.GetProperty(property);
        int[][] grid = new int[rows.GetArrayLength()][];
        int row = 0;
        foreach (JsonElement values in rows.EnumerateArray())
        {
            int[] cells = new int[values.GetArrayLength()];
            int column = 0;
            foreach (JsonElement cell in values.EnumerateArray())
            {
                cells[column++] = cell.GetInt32();
            }

            grid[row++] = cells;
        }

        return grid;
    }

    /// <summary>Reads a rectangular grid of labels given as an array of rows.</summary>
    public static string[][] LabelGrid(JsonElement element, string property)
    {
        JsonElement rows = element.GetProperty(property);
        string[][] grid = new string[rows.GetArrayLength()][];
        int row = 0;
        foreach (JsonElement values in rows.EnumerateArray())
        {
            string[] cells = new string[values.GetArrayLength()];
            int column = 0;
            foreach (JsonElement cell in values.EnumerateArray())
            {
                cells[column++] = cell.GetString() ?? throw new InvalidOperationException("A label was null.");
            }

            grid[row++] = cells;
        }

        return grid;
    }

    /// <summary>Reads an array of doubles.</summary>
    public static double[] Doubles(JsonElement element, string property)
    {
        JsonElement values = element.GetProperty(property);
        double[] result = new double[values.GetArrayLength()];
        int i = 0;
        foreach (JsonElement value in values.EnumerateArray())
        {
            result[i++] = value.GetDouble();
        }

        return result;
    }

    private static JsonDocument Load()
    {
        using Stream stream = typeof(SpecVectors).Assembly.GetManifestResourceStream("spec_vectors.json")
            ?? throw new InvalidOperationException(
                "spec_vectors.json is not embedded in the test assembly.");
        return JsonDocument.Parse(stream);
    }
}
