using System.Globalization;
using System.Text;
using SynthGen.Core.Generation;

namespace SynthGen.Core.Load;

/// <summary>Writes generated rows to CSV — the no-database path for dry runs and inspection.</summary>
public static class CsvWriter
{
    public static long WriteFile(RowGenerator generator, string path)
    {
        using var writer = new StreamWriter(path, append: false, Encoding.UTF8);
        return Write(generator, writer);
    }

    private static long Write(RowGenerator generator, TextWriter writer)
    {
        writer.WriteLine(string.Join(",", generator.Columns.Select(c => Escape(c.Column.Name))));

        long count = 0;
        foreach (var row in generator.Rows())
        {
            writer.WriteLine(string.Join(",", row.Select(Format)));
            count++;
        }
        return count;
    }

    private static string Format(object? value) => value switch
    {
        null => "",
        bool b => b ? "1" : "0",
        byte[] bytes => "0x" + Convert.ToHexString(bytes),
        DateTime dt => dt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        DateTimeOffset dto => dto.ToString("yyyy-MM-dd HH:mm:sszzz", CultureInfo.InvariantCulture),
        TimeSpan ts => ts.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture),
        _ => Escape(Convert.ToString(value, CultureInfo.InvariantCulture) ?? ""),
    };

    private static string Escape(string s) =>
        s.Contains(',') || s.Contains('"') || s.Contains('\n') || s.Contains('\r')
            ? $"\"{s.Replace("\"", "\"\"")}\""
            : s;
}
