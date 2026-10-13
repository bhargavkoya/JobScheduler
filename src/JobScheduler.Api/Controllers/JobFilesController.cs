using JobScheduler.Application.Auth;
using JobScheduler.Application.Reconciliation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace JobScheduler.Api.Controllers;

/// <summary>Uploads of CSV files that a file reconciliation job will compare. Parsed on arrival so the UI can offer the real columns.</summary>
[ApiController]
[Authorize]
[Route("job-files")]
public class JobFilesController(IInputFileService files) : ControllerBase
{
    /// <summary>multipart/form-data with one or more parts named "files". Returns each file's alias, columns, row count and a preview.</summary>
    [HttpPost]
    [RequestSizeLimit(InputFileService.MaxFileBytes * InputFileService.MaxFilesPerUpload + 1024 * 1024)]
    public async Task<ActionResult<IReadOnlyList<InputFileDto>>> Upload(
        [FromForm(Name = "files")] List<IFormFile> upload, CancellationToken ct)
    {
        var contents = new List<FileUpload>();
        foreach (var file in upload)
        {
            // Refuse before reading: the service re-checks, but a huge part should not be buffered first.
            if (file.Length > InputFileService.MaxFileBytes)
                throw new ValidationException(
                    $"'{file.FileName}' is larger than {InputFileService.MaxFileBytes / (1024 * 1024)} MB.");

            using var buffer = new MemoryStream((int)file.Length);
            await file.CopyToAsync(buffer, ct);
            contents.Add(new FileUpload(file.FileName, buffer.ToArray()));
        }

        return Ok(await files.UploadAsync(contents, ct));
    }
}
