using System.Text;
using JobScheduler.Application.Auth;

namespace JobScheduler.Application.Reconciliation;

/// <summary>A parsed CSV file: a header row plus data rows, all kept as text. Numbers are parsed only where a rule needs them.</summary>
public sealed class CsvTable
{
    public const int MaxRows = 50_000;
    public const int MaxColumns = 100;

    private readonly Dictionary<string, int> _index;

    public CsvTable(IReadOnlyList<string> headers, IReadOnlyList<string[]> rows)
    {
        Headers = headers;
        Rows = rows;
        _index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < headers.Count; i++) _index[headers[i]] = i;
    }

    public IReadOnlyList<string> Headers { get; }
    public IReadOnlyList<string[]> Rows { get; }

    /// <summary>Zero-based column position, matched case-insensitively. -1 when the column does not exist.</summary>
    public int IndexOf(string column) => _index.GetValueOrDefault(column.Trim(), -1);

    /// <summary>
    /// RFC 4180 parsing: quoted fields, doubled quotes, line breaks inside quotes, CRLF/LF/CR, optional BOM.
    /// The delimiter (comma, semicolon, tab or pipe) is detected from the header line.
    /// </summary>
    public static CsvTable Parse(byte[] content)
    {
        var text = new UTF8Encoding(false).GetString(content);
        if (text.Length > 0 && text[0] == '﻿') text = text[1..];

        var delimiter = DetectDelimiter(text);
        var records = new List<List<string>>();
        var field = new StringBuilder();
        var current = new List<string>();
        var inQuotes = false;
        var fieldWasQuoted = false;

        void EndField()
        {
            current.Add(field.ToString());
            field.Clear();
            fieldWasQuoted = false;
        }

        void EndRecord()
        {
            var lastWasQuoted = fieldWasQuoted;
            EndField();
            // A line with only one empty, unquoted field is a blank line, not a record.
            if (!(current.Count == 1 && current[0].Length == 0 && !lastWasQuoted)) records.Add(current);
            current = new List<string>();
        }

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                    else inQuotes = false;
                }
                else field.Append(c);
                continue;
            }

            if (c == '"' && field.Length == 0) { inQuotes = true; fieldWasQuoted = true; }
            else if (c == delimiter) EndField();
            else if (c == '\r') { if (i + 1 < text.Length && text[i + 1] == '\n') i++; EndRecord(); }
            else if (c == '\n') EndRecord();
            else field.Append(c);
        }

        if (inQuotes) throw new ValidationException("The file has an opening quote that is never closed.");
        if (field.Length > 0 || current.Count > 0 || fieldWasQuoted) EndRecord();

        if (records.Count == 0) throw new ValidationException("The file is empty.");

        var headers = records[0].Select(h => h.Trim()).ToList();
        ValidateHeaders(headers);

        if (records.Count - 1 > MaxRows)
            throw new ValidationException($"The file has more than {MaxRows:N0} data rows.");

        var rows = new List<string[]>(records.Count - 1);
        for (var r = 1; r < records.Count; r++)
        {
            var record = records[r];
            if (record.Count != headers.Count)
                throw new ValidationException(
                    $"Row {r} has {record.Count} fields but the header has {headers.Count}. Check for an unquoted comma or a missing value.");
            rows.Add(record.ToArray());
        }

        return new CsvTable(headers, rows);
    }

    private static void ValidateHeaders(List<string> headers)
    {
        if (headers.Count > MaxColumns)
            throw new ValidationException($"The file has more than {MaxColumns} columns.");

        for (var i = 0; i < headers.Count; i++)
            if (headers[i].Length == 0)
                throw new ValidationException($"Column {i + 1} has no name in the header row.");

        var duplicate = headers.GroupBy(h => h, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
            throw new ValidationException($"The header row repeats the column name '{duplicate.Key}'.");
    }

    private static char DetectDelimiter(string text)
    {
        var inQuotes = false;
        var counts = new Dictionary<char, int> { [','] = 0, [';'] = 0, ['\t'] = 0, ['|'] = 0 };
        foreach (var c in text)
        {
            if (c == '"') inQuotes = !inQuotes;
            else if (!inQuotes && (c == '\n' || c == '\r')) break;
            else if (!inQuotes && counts.ContainsKey(c)) counts[c]++;
        }

        var best = counts.OrderByDescending(kv => kv.Value).First();
        return best.Value == 0 ? ',' : best.Key;
    }
}
