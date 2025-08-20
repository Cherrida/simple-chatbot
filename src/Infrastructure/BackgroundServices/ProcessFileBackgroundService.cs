using System.Net.Mime;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using ChatbotApi.Application.Chatbots.Commands.CreatePreMessageFromFileCommand;
using ChatbotApi.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using DocumentFormat.OpenXml.Wordprocessing;
using Text = DocumentFormat.OpenXml.Wordprocessing.Text;

using OpenAiService.Splitters;

#pragma warning disable CS8604 // Possible null reference argument.
#pragma warning disable CS8602 // Dereference of a possibly null reference.

namespace ChatbotApi.Infrastructure.BackgroundServices;

public class ProcessFileBackgroundService : BackgroundService
{
    private readonly ChannelReader<ProcessFileBackgroundTask> _channelReader;
    private readonly ChannelWriter<ProcessFileBackgroundTask> _channelWriter;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<ProcessFileBackgroundService> _logger;

    public ProcessFileBackgroundService(
        ChannelReader<ProcessFileBackgroundTask> channelReader,
        IServiceProvider serviceProvider,
        ILogger<ProcessFileBackgroundService> logger, ChannelWriter<ProcessFileBackgroundTask> channelWriter)
    {
        _channelReader = channelReader;
        _serviceProvider = serviceProvider;
        _logger = logger;
        _channelWriter = channelWriter;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        await foreach (var task in _channelReader.ReadAllAsync(stoppingToken))
        {
            using var scope = _serviceProvider.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<IApplicationDbContext>();
            var openAiService = scope.ServiceProvider.GetRequiredService<IOpenAiService>();
            try
            {
                // Processing logic from original handler
                await ProcessFileTaskAsync(task, context, openAiService, stoppingToken);
                _logger.LogInformation("Processed file {FileName} for ChatBot {ChatBotId}", task.FileName,
                    task.ChatBotId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing file background task");
            }
        }
    }

    private async Task ProcessFileTaskAsync(
        ProcessFileBackgroundTask task,
        IApplicationDbContext context,
        IOpenAiService openAiService,
        CancellationToken cancellationToken)
    {
        try
        {
            if (task.FileContent == null) return;

            var chatbot = await context.Chatbots
                .FirstAsync(c => c.Id == task.ChatBotId, cancellationToken);

            if (chatbot.LlmKey == null) return;

            using var sha256 = SHA256.Create();
            var fileHash = BitConverter.ToString(sha256.ComputeHash(task.FileContent)).Replace("-", "");

            var existingMessages1 = await context.PreMessages
                .Where(p => p.ChatBotId == task.ChatBotId && p.FileHash == fileHash && p.FileHash != null)
                .ToListAsync(cancellationToken);

            var existingMessages2 = await context.PreMessages
                .Where(p => p.ChatBotId == task.ChatBotId && p.Url == task.Url && p.Url != null)
                .ToListAsync(cancellationToken);
            
            var existingMessages = existingMessages1.Union(existingMessages2).ToList();
            var fileExtension = Path.GetExtension(task.FileName).ToLower();

            if (!string.IsNullOrEmpty(task.Url) && task.FileMimeType == MediaTypeNames.Text.Html)
            {
                // For HTML content processing
                var maxOrder = await context.PreMessages
                    .Where(p => p.ChatBotId == task.ChatBotId && p.Order < 10000)
                    .MaxAsync(p => (int?)p.Order, cancellationToken) ?? 0;

                var content = await openAiService.GetHtmlContentAsync(task.Url, chatbot.LlmKey,  chatbot.ModelName, cancellationToken);
                var splitter = new RecursiveCharacterTextSplitter(
                    chunkSize: task.ChunkSize,
                    chunkOverlap: task.OverlappingSize
                );
                var chunks = splitter.SplitText(content);

                var preMessages = new List<PreMessage>();
                var startOrder = maxOrder + 1;
                var newOrder = new List<int>();

                // First, create all new PreMessages
                foreach (var chunk in chunks.Select((text, index) => new { Text = text, Index = index }))
                {
                    newOrder.Add(startOrder + chunk.Index);
                    var preMessage = new PreMessage
                    {
                        ChatBotId = task.ChatBotId,
                        Order = startOrder + chunk.Index,
                        UserMessage = chunk.Text,
                        AssistantMessage = string.Empty,
                        FileName = task.Url,
                        FileHash = fileHash,
                        FileMimeType = "text/plaintext",
                        IsRequired = task.IsRequired,
                        UseCag = false,
                        Url = task.Url,
                        ChunkSize = task.ChunkSize,
                        OverlappingSize = task.OverlappingSize,
                        LastUpdate = DateTime.UtcNow,
                        CronJob = task.CronJob
                        
                    };

                    if (chatbot.LlmKey != null)
                    {
                        var embeddings = await openAiService.CallEmbeddingsAsync(
                            preMessage.UserMessage,
                            chatbot.LlmKey,
                            cancellationToken
                        );
                        preMessage.Embedding = embeddings;
                    }

                    preMessages.Add(preMessage);
                }

                // Now remove existing messages since this is UseCag = false
                if (existingMessages.Any())
                {
                    var oldOrderString = string.Join(",", existingMessages.Select(e => e.Order));
                    var newOrderString = string.Join(",", newOrder);
                    var updateInfo = new RefreshInformation()
                    {
                        ChatBotId = task.ChatBotId,
                        Created = DateTime.UtcNow,
                        FileName = task.FileName,
                        Url = task.Url,
                        IsDismissed = false,
                        ExceptionMessage = $"เปลี่ยนลำดับจาก {oldOrderString} เป็น {newOrderString}"
                    };
                    await context.RefreshInformation.AddAsync(updateInfo, cancellationToken);

                    context.PreMessages.RemoveRange(existingMessages);
                    await context.SaveChangesAsync(cancellationToken);
                }

                // Add new messages
                await context.PreMessages.AddRangeAsync(preMessages, cancellationToken);
                await context.SaveChangesAsync(cancellationToken);
            }
            else if (task.UseCag || task is { FileContent: not null, Url: not null })
            {
                // Check if there's an existing CAG PreMessage to update
                var existingCagMessage = existingMessages.FirstOrDefault(m => m.UseCag ?? false);

                if (existingCagMessage != null)
                {
                    // Update existing PreMessage instead of removing it
                    existingCagMessage.FileName = task.FileName;
                    if (existingCagMessage.PreMessageContentId != null)
                    {
                        var fileContent = await context.PreMessageContents.FirstOrDefaultAsync(
                            x => x.Id == existingCagMessage.PreMessageContentId, cancellationToken);
                        if (fileContent != null)
                        {
                            fileContent.Content = task.FileContent;
                            context.PreMessageContents.Update(fileContent);
                        }
                        else
                        {
                            var newFileContent = new PreMessageContent()
                            {
                                Content = task.FileContent
                            };
                            await context.PreMessageContents.AddAsync(newFileContent, cancellationToken);
                            existingCagMessage.PreMessageContentId = newFileContent.Id;
                        }
                    }
                    
                    existingCagMessage.FileMimeType = task.FileMimeType ?? "application/pdf";
                    existingCagMessage.IsRequired = task.IsRequired;
                    existingCagMessage.LastUpdate = DateTime.UtcNow;
                    existingCagMessage.CronJob = task.CronJob;
                    existingCagMessage.Url = task.Url;
                    existingCagMessage.ChunkSize = task.ChunkSize;
                    existingCagMessage.OverlappingSize = task.OverlappingSize;
                    
                    if (chatbot.LlmKey != null)
                    {
                        var base64 = Convert.ToBase64String(task.FileContent);
                        var summary = await openAiService.CallSummaryAsync(
                            base64,
                            task.FileMimeType ?? "application/pdf",
                            chatbot.LlmKey, chatbot.ModelName,
                            cancellationToken);

                        existingCagMessage.UserMessage = summary;
                        var embeddings = await openAiService.CallEmbeddingsAsync(
                            existingCagMessage.UserMessage,
                            chatbot.LlmKey,
                            cancellationToken);

                        existingCagMessage.Embedding = embeddings;
                    }

                    var updateInfo = new RefreshInformation()
                    {
                        ChatBotId = task.ChatBotId,
                        Created = DateTime.UtcNow,
                        FileName = task.FileName,
                        Url = task.Url,
                        IsDismissed = false,
                        ExceptionMessage = $"ลำดับ {existingCagMessage.Order} อัพเดตเนื้อหาใหม่"
                    };
                    await context.RefreshInformation.AddAsync(updateInfo, cancellationToken);
                    await context.SaveChangesAsync(cancellationToken);
                }
                else
                {
                    // For CAG (Content-Aware Generation) processing - Update instead of remove
                    var maxOrder = await context.PreMessages
                        .Where(p => p.ChatBotId == task.ChatBotId)
                        .MaxAsync(p => (int?)p.Order, cancellationToken) ?? 9999;

                    var startOrder = Math.Max(10000, maxOrder + 1);

                    // Create new CAG PreMessage
                    var preMessage = new PreMessage
                    {
                        ChatBotId = task.ChatBotId,
                        Order = startOrder,
                        UserMessage = string.Empty,
                        AssistantMessage = string.Empty,
                        FileName = task.FileName,
                        FileHash = fileHash,
                        FileMimeType = task.FileMimeType ?? "application/pdf",
                        IsRequired = task.IsRequired,
                        UseCag = true,
                        LastUpdate = DateTime.UtcNow,
                        CronJob = task.CronJob,
                        Url = task.Url,
                        ChunkSize = task.ChunkSize,
                        OverlappingSize = task.OverlappingSize
                    };
                    
                    preMessage.PreMessageContent = new PreMessageContent()
                    {
                        Content = task.FileContent
                    };

                    if (chatbot.LlmKey != null)
                    {
                        var base64 = Convert.ToBase64String(task.FileContent);
                        var summary = await openAiService.CallSummaryAsync(
                            base64,
                            task.FileMimeType ?? "application/pdf",
                            chatbot.LlmKey, chatbot.ModelName,
                            cancellationToken);

                        preMessage.UserMessage = summary;
                        var embeddings = await openAiService.CallEmbeddingsAsync(
                            preMessage.UserMessage,
                            chatbot.LlmKey,
                            cancellationToken);

                        preMessage.Embedding = embeddings;
                    }

                    var updateInfo = new RefreshInformation()
                    {
                        ChatBotId = task.ChatBotId,
                        Created = DateTime.UtcNow,
                        FileName = task.FileName,
                        Url = task.Url,
                        IsDismissed = false,
                        ExceptionMessage = $"ลำดับ {preMessage.Order} เพิ่มเนื้อหาใหม่"
                    };
                    await context.RefreshInformation.AddAsync(updateInfo, cancellationToken);

                    await context.PreMessages.AddAsync(preMessage, cancellationToken);
                    await context.SaveChangesAsync(cancellationToken);
                }
            }
            else if (task.FileContent != null)
            {
                List<string> chunks;
                if (fileExtension == ".docx")
                {
                    var text = ReadTextFromDocx(task.FileContent);
                    var splitter = new RecursiveCharacterTextSplitter(
                        chunkSize: task.ChunkSize,
                        chunkOverlap: task.OverlappingSize);
                    chunks = splitter.SplitText(text);
                }
                else if (fileExtension == ".xlsx")
                {
                    var text = ReadTextFromXlsx(task.FileContent);
                    var splitter = new RecursiveCharacterTextSplitter(
                        chunkSize: task.ChunkSize,
                        chunkOverlap: task.OverlappingSize);
                    chunks = splitter.SplitText(text);
                }
                else if (fileExtension == ".pdf")
                {
                    var base64 = Convert.ToBase64String(task.FileContent);
                    chunks = await openAiService.GetTextChunksFromFileAsync(
                        base64, "application/pdf", chatbot.LlmKey!, chatbot.ModelName, null, task.ChunkSize, task.OverlappingSize,
                        cancellationToken);
                }
                else
                {
                    throw new NotSupportedException($"Unsupported file type: {fileExtension}");
                }

                var maxOrder = await context.PreMessages
                    .Where(p => p.ChatBotId == task.ChatBotId)
                    .MaxAsync(p => (int?)p.Order, cancellationToken) ?? 9999;

                var startOrder = Math.Max(10000, maxOrder + 1);
                var newOrder = new List<int>();

                var preMessages = new List<PreMessage>();
                foreach (var chunk in chunks.Select((text, index) => new { Text = text, Index = index }))
                {
                    newOrder.Add(startOrder + chunk.Index);
                    var preMessage = new PreMessage
                    {
                        ChatBotId = task.ChatBotId,
                        Order = startOrder + chunk.Index,
                        UserMessage = chunk.Text,
                        AssistantMessage = string.Empty,
                        FileName = task.Url,
                        FileHash = fileHash,
                        FileMimeType = "text/plaintext",
                        IsRequired = task.IsRequired,
                        UseCag = false,
                        Url = task.Url,
                        ChunkSize = task.ChunkSize,
                        OverlappingSize = task.OverlappingSize,
                        LastUpdate = DateTime.UtcNow,
                        CronJob = task.CronJob
                    };

                    if (chatbot.LlmKey != null)
                    {
                        var embeddings = await openAiService.CallEmbeddingsAsync(
                            preMessage.UserMessage,
                            chatbot.LlmKey,
                            cancellationToken
                        );
                        preMessage.Embedding = embeddings;
                    }

                    preMessages.Add(preMessage);
                }

                // Add new messages first
                await context.PreMessages.AddRangeAsync(preMessages, cancellationToken);

                // Then remove existing messages only after successful creation
                if (existingMessages.Any())
                {
                    var oldOrderString = string.Join(",", existingMessages.Select(e => e.Order));
                    var newOrderString = string.Join(",", newOrder);
                    var updateInfo = new RefreshInformation()
                    {
                        ChatBotId = task.ChatBotId,
                        Created = DateTime.UtcNow,
                        FileName = task.FileName,
                        Url = task.Url,
                        IsDismissed = false,
                        ExceptionMessage = $"เปลี่ยนลำดับจาก {oldOrderString} เป็น {newOrderString}"
                    };
                    await context.RefreshInformation.AddAsync(updateInfo, cancellationToken);
                    context.PreMessages.RemoveRange(existingMessages);
                }
                else
                {
                    var newOrderString = string.Join(",", newOrder);
                    var updateInfo = new RefreshInformation()
                    {
                        ChatBotId = task.ChatBotId,
                        Created = DateTime.UtcNow,
                        FileName = task.FileName,
                        Url = task.Url,
                        IsDismissed = false,
                        ExceptionMessage = $"เพื่อเนื้อหาที่ลำดับ {newOrderString}"
                    };
                    await context.RefreshInformation.AddAsync(updateInfo, cancellationToken);
                }

                await context.SaveChangesAsync(cancellationToken);
            }
        }
        catch (Exception e)
        {
            var importError = new ImportError()
            {
                ChatBotId = task.ChatBotId,
                ChunkSize = task.ChunkSize,
                OverlappingSize = task.OverlappingSize,
                UseCag = task.UseCag,
                FileName = task.FileName,
                FileMimeType = task.FileMimeType,
                FileContent = task.FileContent,
                IsRequired = task.IsRequired,
                Url = task.Url,
                ExceptionMessage = e.Message,
                IsDismissed = false,
                Created = DateTime.UtcNow
            };
            await context.ImportErrors.AddAsync(importError, cancellationToken);
            await context.SaveChangesAsync(cancellationToken);
            await Task.Delay(2000, cancellationToken);
            throw;
        }
    }

    private static string ReadTextFromXlsx(byte[] fileContent)
    {
        using var stream = new MemoryStream(fileContent);
        using SpreadsheetDocument doc = SpreadsheetDocument.Open(stream, false);

        var workbookPart = doc.WorkbookPart;
        var sharedStringTable = workbookPart?.SharedStringTablePart?.SharedStringTable;
        var text = new StringBuilder();

        foreach (var sheet in workbookPart.Workbook.Descendants<Sheet>())
        {
            var worksheetPart = (WorksheetPart)workbookPart.GetPartById(sheet.Id);
            var rows = worksheetPart.Worksheet.Descendants<Row>().ToList();

            if (rows.Count == 0) continue;

            // Add sheet header
            text.AppendLine($"# {sheet.Name}");

            // Build markdown table
            text.AppendLine("| " + string.Join(" | ", rows.First().Descendants<Cell>()
                .Select(c => SanitizeCellValue(c, sharedStringTable))) + " |");
            text.AppendLine("| " + string.Join(" | ", rows.First().Descendants<Cell>()
                .Select(_ => "---")) + " |");

            foreach (var row in rows.Skip(1))
            {
                text.AppendLine("| " + string.Join(" | ", row.Descendants<Cell>()
                    .Select(c => SanitizeCellValue(c, sharedStringTable))) + " |");
            }

            text.AppendLine(); // Add spacing between tables
        }

        return text.ToString();
    }

    // Helper to get cell values safely
    private static string SanitizeCellValue(Cell cell, SharedStringTable sharedStringTable)
    {
        string value = cell switch
        {
            _ when cell.DataType?.Value == CellValues.SharedString
                => sharedStringTable?.ElementAt(int.Parse(cell.InnerText))?.InnerText ?? string.Empty,
            _ => cell.InnerText
        };

        // Sanitize characters that break markdown tables
        return value.Replace("|", "\\|") // Escape pipes
            .Replace("\r", " ") // Remove carriage returns
            .Replace("\n", " "); // Remove newlines
    }

    private static string ReadTextFromDocx(byte[] fileContent)
    {
        using var stream = new MemoryStream(fileContent);
        using WordprocessingDocument doc = WordprocessingDocument.Open(stream, false);

        var text = new StringBuilder();

        // Process the main document body
        if (doc.MainDocumentPart?.Document.Body != null)
        {
            foreach (var paragraph in doc.MainDocumentPart.Document.Body.Descendants<Paragraph>())
            {
                // Join all text elements in the paragraph
                var paragraphText = string.Join("", paragraph.Descendants<Text>().Select(t => t.Text));
                text.AppendLine(paragraphText); // Append each paragraph as a new line
            }
        }

        // Process headers
        foreach (var headerPart in doc.MainDocumentPart?.HeaderParts ?? Enumerable.Empty<HeaderPart>())
        {
            foreach (var paragraph in headerPart.Header.Descendants<Paragraph>())
            {
                var paragraphText = string.Join("", paragraph.Descendants<Text>().Select(t => t.Text));
                text.AppendLine(paragraphText); // Append each paragraph as a new line
            }
        }

        // Process footers
        foreach (var footerPart in doc.MainDocumentPart?.FooterParts ?? Enumerable.Empty<FooterPart>())
        {
            foreach (var paragraph in footerPart.Footer.Descendants<Paragraph>())
            {
                var paragraphText = string.Join("", paragraph.Descendants<Text>().Select(t => t.Text));
                text.AppendLine(paragraphText); // Append each paragraph as a new line
            }
        }

        // Return the result without replacing spaces or new lines
        return text.ToString().Replace("\r\n", "\n");
    }
}
