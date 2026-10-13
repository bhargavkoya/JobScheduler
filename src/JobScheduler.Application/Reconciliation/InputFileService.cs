using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using JobScheduler.Application.Auth;
using JobScheduler.Application.Common;
using JobScheduler.Domain.Reconciliation;

namespace JobScheduler.Application.Reconciliation;

public interface IInputFileService
{
    /// <summary>Parses each CSV right away (headers, row count, preview) and stores it unattached, owned by the caller.</summary>
    Task<IReadOnlyList<InputFileDto>> UploadAsync(IReadOnlyList<FileUpload> files, CancellationToken ct);
}

public partial class InputFileService(IReconciliationStore store, ICurrentUser me, TimeProvider clock) : IInputFileService
{
    public const int MaxFileBytes = 5 * 1024 * 1024;
    public const int MaxFilesPerUpload = 5;
    private const int PreviewRows = 5;

    public async Task<IReadOnlyList<InputFileDto>> UploadAsync(IReadOnlyList<FileUpload> files, CancellationToken ct)
    {
        if (files.Count == 0) throw new ValidationException("Choose at least one CSV file to upload.");
        if (files.Count > MaxFilesPerUpload) throw new ValidationException($"Upload at most {MaxFilesPerUpload} files at a time.");

        // Parse everything first: if any file is bad nothing is stored.
        var parsed = new List<(FileUpload Upload, CsvTable Table)>();
        foreach (var upload in files)
        {
            var name = Path.GetFileName(upload.FileName ?? "");
            if (!name.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
                throw new ValidationException($"'{name}': only .csv files are supported.");
            if (upload.Content.Length == 0) throw new ValidationException($"'{name}' is empty.");
            if (upload.Content.Length > MaxFileBytes)
                throw new ValidationException($"'{name}' is larger than {MaxFileBytes / (1024 * 1024)} MB.");

            try
            {
                parsed.Add((upload with { FileName = name }, CsvTable.Parse(upload.Content)));
            }
            catch (ValidationException ex)
            {
                throw new ValidationException($"'{name}': {ex.Message}");
            }
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var usedAliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<InputFileDto>();
        foreach (var (upload, table) in parsed)
        {
            var file = new InputFile
            {
                OwnerId = me.UserId,
                Alias = UniqueAlias(upload.FileName, usedAliases),
                FileName = upload.FileName,
                SizeBytes = upload.Content.Length,
                Sha256 = Convert.ToHexString(SHA256.HashData(upload.Content)).ToLowerInvariant(),
                HeadersJson = JsonSerializer.Serialize(table.Headers),
                RowCount = table.Rows.Count,
                UploadedAtUtc = now
            };
            store.AddFile(file, upload.Content);
            result.Add(file.ToDto(table.Rows.Take(PreviewRows).Select(r => (IReadOnlyList<string>)r).ToList()));
        }

        await store.SaveChangesAsync(ct);
        return result;
    }

    /// <summary>"Internal Trades (Sept).csv" becomes "Internal_Trades_Sept"; clashes get a numeric suffix.</summary>
    internal static string UniqueAlias(string fileName, ISet<string> used)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var alias = NonWord().Replace(stem, "_").Trim('_');
        // An alias must start with a letter ("1_internal_trades" becomes "internal_trades").
        var firstLetter = alias.ToCharArray().ToList().FindIndex(char.IsAsciiLetter);
        alias = firstLetter < 0 ? "" : alias[firstLetter..];
        if (alias.Length > 24) alias = alias[..24];
        alias = alias.TrimEnd('_');
        if (alias.Length == 0) alias = "file";

        var candidate = alias;
        for (var n = 2; !used.Add(candidate); n++) candidate = $"{alias}_{n}";
        return candidate;
    }

    [GeneratedRegex("[^A-Za-z0-9]+")]
    private static partial Regex NonWord();
}

internal static class InputFileMapping
{
    public static IReadOnlyList<string> HeaderList(this InputFile f) =>
        JsonSerializer.Deserialize<List<string>>(f.HeadersJson) ?? [];

    public static InputFileDto ToDto(this InputFile f, IReadOnlyList<IReadOnlyList<string>>? preview = null) =>
        new(f.Id, f.Alias, f.FileName, f.HeaderList(), f.RowCount, f.SizeBytes, f.UploadedAtUtc, preview ?? []);
}
