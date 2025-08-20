using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using ChatbotApi.Application.Common.Interfaces;
using ChatbotApi.Application.Common.Models;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Caching.Distributed;
using ChatbotApi.Application.Common.Extensions;

namespace ChatbotApi.Infrastructure.Processors.UcanFileProcessor
{
    public class UcanFileProcessor : ILineMessageProcessor
    {
        public string Name => ChatbotApi.Domain.Constants.Systems.UcanFile;

        private readonly ILogger<UcanFileProcessor> _logger;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IWebHostEnvironment _environment;
        private readonly IDistributedCache _cache;
        private readonly ISystemService? _systemService;

        // Allowed extensions (lowercase, with dot)
        private static readonly string[] AllowedExtensions = new[] {
            ".pdf", ".docx", ".pptx", ".xlsx", ".txt", ".csv",
            ".doc", ".xls", ".xlsm", ".xltx", ".xlt", ".ppt",
            ".ppsx", ".pps", ".potx"
        };

        // Organization list
        private static readonly string[] Organizations = new[] {
            "บริษัท อสมท จำกัด",
            "กรมโยธาธิการและผังเมือง",
        };

        public UcanFileProcessor(
            ILogger<UcanFileProcessor> logger,
            IHttpClientFactory httpClientFactory,
            IWebHostEnvironment environment,
            IDistributedCache cache,
            ISystemService? systemService = null)
        {
            _logger = logger;
            _httpClientFactory = httpClientFactory;
            _environment = environment;
            _cache = cache;
            _systemService = systemService;
        }

        public async Task<LineReplyStatus> ProcessLineAsync(LineEvent evt, int chatbotId, string message, string userId, string replyToken, CancellationToken cancellationToken = default)
        {
            // Check if user is responding with organization name or in retrieval flow
            var cacheKey = $"ucanfile_{userId}";
            var cachedFileInfo = await _cache.GetObjectAsync<CachedFileInfo>(cacheKey);
            
            // Check if user wants to retrieve files
            if (message != null && message.Trim().Equals("GET", StringComparison.OrdinalIgnoreCase))
            {
                // Initiate file retrieval flow
                var orgList = string.Join("\n- ", Organizations);
                var promptMessage = $"โปรดเลือกหน่วยงานที่ต้องการดึงไฟล์:\n- {orgList}";
                
                // Cache that we're in retrieval mode
                var retrievalState = new CachedFileInfo
                {
                    IsRetrievalMode = true
                };
                await _cache.SetObjectAsync(cacheKey, retrievalState, 1440); // Cache for 24 hours
                
                return new LineReplyStatus
                {
                    Status = 200,
                    ReplyMessage = new LineReplyMessage
                    {
                        ReplyToken = replyToken,
                        Messages = new System.Collections.Generic.List<LineMessage>
                        {
                            new LineTextMessage(promptMessage)
                        }
                    }
                };
            }
            
            if (cachedFileInfo != null)
            {
                // Handle retrieval flow
                if (cachedFileInfo.IsRetrievalMode)
                {
                    return await HandleFileRetrievalFlowAsync(message, userId, replyToken, cachedFileInfo, cacheKey);
                }
                
                // Handle file upload flow - User is providing organization name
                var orgName = message?.Trim();
                if (string.IsNullOrWhiteSpace(orgName))
                {
                    var orgList = string.Join("\n- ", Organizations);
                    var promptMessage = $"โปรดระบุชื่อหน่วยงานที่ต้องการเก็บไฟล์เอกสารนี้:\n- {orgList}";
                    
                    return new LineReplyStatus
                    {
                        Status = 200,
                        ReplyMessage = new LineReplyMessage
                        {
                            ReplyToken = replyToken,
                            Messages = new System.Collections.Generic.List<LineMessage>
                            {
                                new LineTextMessage(promptMessage)
                            }
                        }
                    };
                }
                
                // Find closest matching organization
                var matchedOrg = FindClosestOrganization(orgName);
                
                // Build dated directory path: ucanfile/companyName/ddMMyyyy/
                var today = DateTime.Now;
                var dateFolder = today.ToString("ddMMyyyy");
                var basePath = Path.Combine(_environment.ContentRootPath, "ucanfile", matchedOrg, dateFolder);

                try
                {
                    if (!Directory.Exists(basePath))
                        Directory.CreateDirectory(basePath);

                    var targetPath = Path.Combine(basePath, cachedFileInfo.FileName);

                    await File.WriteAllBytesAsync(targetPath, cachedFileInfo.FileContent, cancellationToken);
                    
                    // Remove cached file info
                    await _cache.RemoveAsync(cacheKey);

                    return new LineReplyStatus
                    {
                        Status = 200,
                        ReplyMessage = new LineReplyMessage
                        {
                            ReplyToken = replyToken,
                            Messages = new System.Collections.Generic.List<LineMessage>
                            {
                                new LineTextMessage($"บันทึกไฟล์ {cachedFileInfo.FileName} สำเร็จแล้วในโฟลเดอร์ {matchedOrg}")
                            }
                        }
                    };
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to save file {FileName} to {BasePath}", cachedFileInfo.FileName, basePath);
                    return new LineReplyStatus
                    {
                        Status = 500,
                        ReplyMessage = new LineReplyMessage
                        {
                            ReplyToken = replyToken,
                            Messages = new System.Collections.Generic.List<LineMessage>
                            {
                                new LineTextMessage("เกิดข้อผิดพลาดในการบันทึกไฟล์ กรุณาลองใหม่อีกครั้ง")
                            }
                        }
                    };
                }
            }
            
            // Prompt user to send document file
            var allowedExtensionsStr = string.Join(", ", AllowedExtensions);
            return new LineReplyStatus
            {
                Status = 200,
                ReplyMessage = new LineReplyMessage
                {
                    ReplyToken = replyToken,
                    Messages = new System.Collections.Generic.List<LineMessage>
                    {
                        new LineTextMessage($"กรุณาส่งไฟล์เอกสาร {allowedExtensionsStr} ที่ต้องการอัปโหลด")
                    }
                }
            };
        }

        public async Task<LineReplyStatus> ProcessLineImageAsync(LineEvent evt, int chatbotId, string messageId, string userId, string replyToken, string accessToken, CancellationToken cancellationToken = default)
        {
            // Download file content from LINE
            var (fileContent, contentType) = await TryDownloadLineFileAsync(messageId, accessToken, cancellationToken);

            if (fileContent == null || fileContent.Length == 0)
            {
                return new LineReplyStatus
                {
                    Status = 400,
                    ReplyMessage = new LineReplyMessage
                    {
                        ReplyToken = replyToken,
                        Messages = new System.Collections.Generic.List<LineMessage>
                        {
                            new LineTextMessage("ไม่สามารถดาวน์โหลดไฟล์จาก LINE ได้")
                        }
                    }
                };
            }

            // Get filename and extension
            string? fileName = evt.Message?.FileName;
            string extension = string.Empty;
            if (!string.IsNullOrWhiteSpace(fileName))
            {
                extension = Path.GetExtension(fileName).ToLowerInvariant();
            }
            else if (!string.IsNullOrWhiteSpace(contentType))
            {
                // Fallback: map contentType to extension
                extension = contentType.ToLowerInvariant() switch
                {
                    "application/pdf" => ".pdf",
                    "application/vnd.openxmlformats-officedocument.wordprocessingml.document" => ".docx",
                    "application/vnd.openxmlformats-officedocument.presentationml.presentation" => ".pptx",
                    "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" => ".xlsx",
                    "application/msword" => ".doc",
                    "application/vnd.ms-excel" => ".xls",
                    "application/vnd.ms-powerpoint" => ".ppt",
                    "text/plain" => ".txt",
                    "text/csv" => ".csv",
                    _ => string.Empty
                };
                fileName = $"{messageId}{extension}";
            }
            else
            {
                fileName = $"{messageId}.bin";
            }

            // Only allow certain file types
            if (!AllowedExtensions.Contains(extension))
            {
                return new LineReplyStatus
                {
                    Status = 400,
                    ReplyMessage = new LineReplyMessage
                    {
                        ReplyToken = replyToken,
                        Messages = new System.Collections.Generic.List<LineMessage>
                        {
                            new LineTextMessage("อนุญาตเฉพาะไฟล์ .pdf, .docx, .pptx, .xlsx, .txt, .csv, .doc, .xls, .xlsm, .xltx, .xlt, .ppt, .ppsx, .pps, .potx เท่านั้น")
                        }
                    }
                };
            }

            // Cache the file content per user ID
            var cacheKey = $"ucanfile_{userId}";
            var cachedFileInfo = new CachedFileInfo
            {
                FileContent = fileContent,
                FileName = fileName,
                Extension = extension
            };
            
            await _cache.SetObjectAsync(cacheKey, cachedFileInfo, 1440); // Cache for 24 hours

            // Ask user for organization name
            var orgList = string.Join("\n- ", Organizations);
            var promptMessage = $"โปรดระบุชื่อหน่วยงานที่ต้องการเก็บไฟล์เอกสารนี้:\n- {orgList}";

            return new LineReplyStatus
            {
                Status = 200,
                ReplyMessage = new LineReplyMessage
                {
                    ReplyToken = replyToken,
                    Messages = new System.Collections.Generic.List<LineMessage>
                    {
                        new LineTextMessage(promptMessage)
                    }
                }
            };
        }

        public Task<LineReplyStatus> ProcessLineImagesAsync(LineEvent mainEvent, int chatbotId, System.Collections.Generic.List<LineEvent> imageEvents, string userId, string replyToken, string accessToken, CancellationToken cancellationToken = default)
        {
            // Only allow single file upload at a time
            return Task.FromResult(new LineReplyStatus
            {
                Status = 200,
                ReplyMessage = new LineReplyMessage
                {
                    ReplyToken = replyToken,
                    Messages = new System.Collections.Generic.List<LineMessage>
                    {
                        new LineTextMessage("กรุณาส่งไฟล์ทีละไฟล์เท่านั้น")
                    }
                }
            });
        }

        public Task<bool> PostProcessLineAsync(string role, string? sourceMessageId, LineSendResponse response, bool isForce = false, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(false);
        }

        public Task<LineReplyStatus> ProcessLocationAsync(LineEvent evt, int chatbotId, double latitude, double longitude, string? address, string userId, string replyToken, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new LineReplyStatus { Status = 404 });
        }

        private async Task<(byte[]?, string?)> TryDownloadLineFileAsync(string messageId, string accessToken, CancellationToken cancellationToken)
        {
            try
            {
                var client = _httpClientFactory.CreateClient("resilient_nocompress");
                client.DefaultRequestHeaders.Add("Authorization", $"Bearer {accessToken}");
                var url = $"https://api-data.line.me/v2/bot/message/{messageId}/content";
                var response = await client.GetAsync(url, cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    string errorDetail = await response.Content.ReadAsStringAsync(cancellationToken);
                    _logger.LogError(
                        "Failed to get content from LINE API for messageId {MessageId}. Status: {StatusCode}. Body: {Body}",
                        messageId, response.StatusCode, errorDetail);
                    return (null, null);
                }

                byte[] contentBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
                string? contentType = response.Content.Headers.ContentType?.MediaType;

                if (string.IsNullOrEmpty(contentType) || contentBytes.Length == 0)
                {
                    _logger.LogWarning("LINE API returned empty content or no content type for messageId: {MessageId}",
                        messageId);
                    return (null, null);
                }

                return (contentBytes, contentType);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Exception while downloading file from LINE API");
                return (null, null);
            }
        }

        private class CachedFileInfo
        {
            public byte[] FileContent { get; set; } = Array.Empty<byte>();
            public string FileName { get; set; } = string.Empty;
            public string Extension { get; set; } = string.Empty;
            
            // For file retrieval flow
            public bool IsRetrievalMode { get; set; } = false;
            public string? SelectedOrganization { get; set; }
            public List<string> FilesInOrganization { get; set; } = new List<string>();
        }

        private string FindClosestOrganization(string input)
        {
            // Exact match first
            foreach (var org in Organizations)
            {
                if (org.Equals(input, StringComparison.OrdinalIgnoreCase))
                {
                    return org;
                }
            }

            // Find closest match using simple string similarity
            string bestMatch = Organizations[0];
            int bestScore = 0;

            foreach (var org in Organizations)
            {
                int score = CalculateSimilarity(input, org);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestMatch = org;
                }
            }

            return bestMatch;
        }

        private int CalculateSimilarity(string source, string target)
        {
            if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(target))
                return 0;

            // Convert to lowercase for case-insensitive comparison
            source = source.ToLowerInvariant();
            target = target.ToLowerInvariant();

            // Use a more sophisticated similarity algorithm - longest common substring
            int[,] l = new int[source.Length + 1, target.Length + 1];
            int lcs = 0;

            for (int i = 0; i <= source.Length; i++)
            {
                for (int j = 0; j <= target.Length; j++)
                {
                    if (i == 0 || j == 0)
                        l[i, j] = 0;
                    else if (source[i - 1] == target[j - 1])
                    {
                        l[i, j] = l[i - 1, j - 1] + 1;
                        if (l[i, j] > lcs)
                            lcs = l[i, j];
                    }
                    else
                        l[i, j] = 0;
                }
            }

            return lcs;
        }
        
        private async Task<LineReplyStatus> HandleFileRetrievalFlowAsync(string? message, string userId, string replyToken, CachedFileInfo cachedFileInfo, string cacheKey)
        {
            // State 1: Waiting for organization name
            if (string.IsNullOrEmpty(cachedFileInfo.SelectedOrganization))
            {
                var orgName = message?.Trim();
                if (string.IsNullOrWhiteSpace(orgName))
                {
                    var orgList = string.Join("\n- ", Organizations);
                    var promptMessage = $"โปรดเลือกหน่วยงานที่ต้องการดึงไฟล์:\n- {orgList}";
                    
                    return new LineReplyStatus
                    {
                        Status = 200,
                        ReplyMessage = new LineReplyMessage
                        {
                            ReplyToken = replyToken,
                            Messages = new System.Collections.Generic.List<LineMessage>
                            {
                                new LineTextMessage(promptMessage)
                            }
                        }
                    };
                }
                
                // Find closest matching organization
                var matchedOrg = FindClosestOrganization(orgName);
                
                // List files in organization directory (all date folders)
                var orgPath = Path.Combine(_environment.ContentRootPath, "ucanfile", matchedOrg);
                var files = new List<string>();
                
                if (Directory.Exists(orgPath))
                {
                    // Get all files from all date folders
                    var dateFolders = Directory.GetDirectories(orgPath);
                    foreach (var dateFolder in dateFolders)
                    {
                        var dateFiles = Directory.GetFiles(dateFolder);
                        foreach (var file in dateFiles)
                        {
                            files.Add(Path.GetFileName(file));
                        }
                    }
                }
                
                if (files.Count == 0)
                {
                    // Remove cached file info
                    await _cache.RemoveAsync(cacheKey);
                    
                    return new LineReplyStatus
                    {
                        Status = 404,
                        ReplyMessage = new LineReplyMessage
                        {
                            ReplyToken = replyToken,
                            Messages = new System.Collections.Generic.List<LineMessage>
                            {
                                new LineTextMessage($"ไม่พบไฟล์ในหน่วยงาน {matchedOrg}")
                            }
                        }
                    };
                }
                
                // Update cache with selected organization and file list
                cachedFileInfo.SelectedOrganization = matchedOrg;
                cachedFileInfo.FilesInOrganization = files;
                await _cache.SetObjectAsync(cacheKey, cachedFileInfo, 1440); // Cache for 24 hours
                
                // List files to user
                var fileList = string.Join("\n- ", files);
                var filePromptMessage = $"ไฟล์ในหน่วยงาน {matchedOrg}:\n- {fileList}\n\nโปรดระบุชื่อไฟล์ที่ต้องการดาวน์โหลด:";
                
                return new LineReplyStatus
                {
                    Status = 200,
                    ReplyMessage = new LineReplyMessage
                    {
                        ReplyToken = replyToken,
                        Messages = new System.Collections.Generic.List<LineMessage>
                        {
                            new LineTextMessage(filePromptMessage)
                        }
                    }
                };
            }
            
            // State 2: Waiting for file name
            var fileName = message?.Trim();
            if (string.IsNullOrWhiteSpace(fileName))
            {
                var fileList = string.Join("\n- ", cachedFileInfo.FilesInOrganization);
                var filePromptMessage = $"ไฟล์ในหน่วยงาน {cachedFileInfo.SelectedOrganization}:\n- {fileList}\n\nโปรดระบุชื่อไฟล์ที่ต้องการดาวน์โหลด:";
                
                return new LineReplyStatus
                {
                    Status = 200,
                    ReplyMessage = new LineReplyMessage
                    {
                        ReplyToken = replyToken,
                        Messages = new System.Collections.Generic.List<LineMessage>
                        {
                            new LineTextMessage(filePromptMessage)
                        }
                    }
                };
            }
            
            // Find closest matching file
            var matchedFile = FindClosestFile(fileName, cachedFileInfo.FilesInOrganization);
            if (string.IsNullOrEmpty(matchedFile))
            {
                var fileList = string.Join("\n- ", cachedFileInfo.FilesInOrganization);
                var filePromptMessage = $"ไม่พบไฟล์ที่ตรงกับ '{fileName}'\nไฟล์ในหน่วยงาน {cachedFileInfo.SelectedOrganization}:\n- {fileList}\n\nโปรดระบุชื่อไฟล์ที่ต้องการดาวน์โหลด:";
                
                return new LineReplyStatus
                {
                    Status = 404,
                    ReplyMessage = new LineReplyMessage
                    {
                        ReplyToken = replyToken,
                        Messages = new System.Collections.Generic.List<LineMessage>
                        {
                            new LineTextMessage(filePromptMessage)
                        }
                    }
                };
            }
            
            // Generate download URL
            // URL format: /ucanfile/{organization}/{dateFolder}/{fileName}
            var orgBasePath = Path.Combine(_environment.ContentRootPath, "ucanfile", cachedFileInfo.SelectedOrganization);
            string? downloadUrl = null;
            string? fullPath = null;
            
            if (Directory.Exists(orgBasePath))
            {
                // Search for the file in all date folders
                var dateFolders = Directory.GetDirectories(orgBasePath);
                foreach (var dateFolder in dateFolders)
                {
                    var filePath = Path.Combine(dateFolder, matchedFile);
                    if (File.Exists(filePath))
                    {
                        fullPath = filePath;
                        var dateFolderName = Path.GetFileName(dateFolder);
                        downloadUrl = $"/ucanfile/{Uri.EscapeDataString(cachedFileInfo.SelectedOrganization)}/{Uri.EscapeDataString(dateFolderName)}/{Uri.EscapeDataString(matchedFile)}";
                        break;
                    }
                }
            }
            
            // Remove cached file info
            await _cache.RemoveAsync(cacheKey);
            
            if (string.IsNullOrEmpty(downloadUrl))
            {
                return new LineReplyStatus
                {
                    Status = 404,
                    ReplyMessage = new LineReplyMessage
                    {
                        ReplyToken = replyToken,
                        Messages = new System.Collections.Generic.List<LineMessage>
                        {
                            new LineTextMessage($"ไม่พบไฟล์ {matchedFile}")
                        }
                    }
                };
            }
            
            return new LineReplyStatus
            {
                Status = 200,
                ReplyMessage = new LineReplyMessage
                {
                    ReplyToken = replyToken,
                    Messages = new System.Collections.Generic.List<LineMessage>
                    {
                        new LineTextMessage($"คลิกที่ลิงก์เพื่อดาวน์โหลดไฟล์:\n{GetFullHostName()}{downloadUrl}")
                    }
                }
            };
        }
        
        private string FindClosestFile(string input, List<string> files)
        {
            // Exact match first
            foreach (var file in files)
            {
                if (file.Equals(input, StringComparison.OrdinalIgnoreCase))
                {
                    return file;
                }
            }
            
            // Find closest match using simple string similarity
            string bestMatch = string.Empty;
            int bestScore = 0;
            
            foreach (var file in files)
            {
                int score = CalculateSimilarity(input, file);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestMatch = file;
                }
            }
            
            return bestMatch;
        }
        
        private string GetFullHostName()
        {
            if (_systemService != null)
            {
                return _systemService.FullHostName;
            }
            
            // Fallback to a default hostname
            return "http://localhost:5000";
        }
    }
}