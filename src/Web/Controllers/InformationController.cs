using ChatbotApi.Application;
using ChatbotApi.Application.Informations.Queries.GetAllTextByFileHashQuery;
using ChatbotApi.Application.Common.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.IO;

namespace ChatbotApi.Web.Controllers;

public class InformationController : MvcController
{
    private readonly IApplicationDbContext _context;

    public InformationController(IApplicationDbContext context)
    {
        _context = context;
    }
    [Route("i/{id}")]
    public async Task<IActionResult> Index(GetAllTextByFileHashQuery query)
    {
        var model = await Mediator.Send(query);
        return View(model);
    }

    [HttpGet]
    [Route("information/getfile")]
    public async Task<IActionResult> GetFile(int? id, string? name, string? userId)
    {
        // New method: Get file by ID from TrackFile table
        if (id.HasValue)
        {
            var trackFile = await _context.TrackFiles.FirstOrDefaultAsync(f => f.Id == id.Value);
            if (trackFile == null)
                return NotFound("File not found.");

            if (!System.IO.File.Exists(trackFile.FilePath))
                return NotFound("Physical file not found.");

            string contentType;
            try
            {
                contentType = MimeTypes.GetMimeType(Path.GetExtension(trackFile.FileName));
            }
            catch
            {
                contentType = trackFile.ContentType ?? "application/octet-stream";
            }

            if (contentType.StartsWith("image/"))
            {
                // Return image inline (no filename, so browser displays it)
                return PhysicalFile(trackFile.FilePath, contentType);
            }
            else
            {
                // For other files, force download with filename
                return PhysicalFile(trackFile.FilePath, contentType, trackFile.FileName);
            }
        }

        // Legacy method: Get file by name and userId (for backward compatibility)
        if (string.IsNullOrWhiteSpace(name))
            return BadRequest("File name or ID required.");

        var filePath = Path.Combine(WebHostEnvironment.ContentRootPath, "storage", userId ?? "", name);

        if (!System.IO.File.Exists(filePath))
            return NotFound();

        string legacyContentType;
        try
        {
            legacyContentType = MimeTypes.GetMimeType(Path.GetExtension(name));
        }
        catch
        {
            legacyContentType = "application/octet-stream";
        }

        if (legacyContentType.StartsWith("image/"))
        {
            // Return image inline (no filename, so browser displays it)
            return PhysicalFile(filePath, legacyContentType);
        }
        else
        {
            // For other files, force download with filename
            return PhysicalFile(filePath, legacyContentType, name);
        }
    }
    
    [HttpGet]
    [Route("ucanfile/{organization}/{dateFolder}/{fileName}")]
    public async Task<IActionResult> GetUcanFile(string organization, string dateFolder, string fileName)
    {
        if (string.IsNullOrWhiteSpace(organization) || string.IsNullOrWhiteSpace(dateFolder) || string.IsNullOrWhiteSpace(fileName))
            return BadRequest("Organization, date folder, and file name are required.");

        // Sanitize inputs to prevent directory traversal attacks
        organization = organization.Replace("..", "").Replace("/", "").Replace("\\", "");
        dateFolder = dateFolder.Replace("..", "").Replace("/", "").Replace("\\", "");
        fileName = fileName.Replace("..", "").Replace("/", "").Replace("\\", "");

        var filePath = Path.Combine(WebHostEnvironment.ContentRootPath, "ucanfile", organization, dateFolder, fileName);

        if (!System.IO.File.Exists(filePath))
            return NotFound("File not found.");

        string contentType;
        try
        {
            contentType = MimeTypes.GetMimeType(Path.GetExtension(fileName));
        }
        catch
        {
            contentType = "application/octet-stream";
        }

        // For security, we'll force download for all ucanfile files
        return PhysicalFile(filePath, contentType, fileName);
    }
}
