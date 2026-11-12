using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using LMLocal.Application.Chat;
using LMLocal.Application.ChatSession;
using LMLocal.Core.Common;
using LMLocal.Core.Models;
using LMLocal.Infrastructure.Tooling.BuiltInVs.Implementations;
using LMLocal.Infrastructure.Tooling.BuiltInVs.Snapshot;
using LMLocal.Infrastructure.VisualStudio;
using LMLocal.Infrastructure.WebView.Messaging;
using LMLocal.Infrastructure.Documents;
using LMLocal.Models;
using Microsoft.VisualStudio.Shell;
using Newtonsoft.Json;

namespace LMLocal.Infrastructure.WebView
{
    /// <summary>
    /// Bridge class for communication between WebView2 and backend logic.
    /// </summary>
    public interface IWebViewBridge
    {
        /// <summary>Executes a prompt request and streams the response to WebView2.</summary>
        Task ExecutePromptAsync(string requestJson);

        /// <summary>Resets the chat history using the specified action.</summary>
        Task<bool> ResetHistoryWithActionAsync(string action);

        /// <summary>Summarizes and compacts the current chat history via the given model.</summary>
        Task<bool> SummarizeAndCompactAsync(string modelId);

        /// <summary>Stops the current text generation process and active tools.</summary>
        Task StopExecutionAsync();

        /// <summary>Loads the snapshot of changed files.</summary>
        Task<bool> GetSnapshotAsync();

        /// <summary>Discards (rolls back) all accepted file changes.</summary>
        Task<bool> DiscardChangesAsync();

        /// <summary>Accepts (commits) all file changes.</summary>
        Task<bool> AcceptChangesAsync();

        /// <summary>Opens a diff viewer for a single changed file.</summary>
        Task<bool> ReviewFileAsync(string filePath);

        /// <summary>Opens diff viewers for all changed files in the JSON list.</summary>
        Task<bool> ReviewAllFilesAsync(string filePathsJson);

        /// <summary>Opens all changed files from the JSON list in the editor.</summary>
        Task<bool> OpenAllFilesAsync(string filePathsJson);

        /// <summary>Discards (rolls back) a single changed file.</summary>
        Task<bool> DiscardFileAsync(string filePath);

        /// <summary>Accepts (commits) a single changed file.</summary>
        Task<bool> AcceptFileAsync(string filePath);
    }


    [ComVisible(true)]
    public class WebViewBridge : IWebViewBridge
    {
        private readonly IWebViewScriptExecutor _scriptExecutor;
        private readonly IGetActiveDocument _activeDocumentTool;
        private readonly ISessionManager _sessionManager;
        private readonly IChatHistoryService _chatHistoryService;
        private readonly ISnapshotManager _snapshotManager;

        internal WebViewBridge(
            IWebViewScriptExecutor scriptExecutor,
            IGetActiveDocument activeDocumentTool,
            ISessionManager sessionManager,
            IChatHistoryService chatHistoryService,
            ISnapshotManager snapshotManager)
        {
            _scriptExecutor = scriptExecutor ?? throw new ArgumentNullException(nameof(scriptExecutor));
            _activeDocumentTool = activeDocumentTool ?? throw new ArgumentNullException(nameof(activeDocumentTool));
            _sessionManager = sessionManager ?? throw new ArgumentNullException(nameof(sessionManager));
            _chatHistoryService = chatHistoryService ?? throw new ArgumentNullException(nameof(chatHistoryService));
            _snapshotManager = snapshotManager ?? throw new ArgumentNullException(nameof(snapshotManager));
            _snapshotManager.SnapshotChangedAsync += OnSnapshotChangedAsync;
        }

        /// <summary>
        /// Executes the provided prompt against LM Studio and streams the response to WebView2.
        /// </summary>
        public async Task ExecutePromptAsync(string requestJson)
        {
            if (string.IsNullOrWhiteSpace(requestJson))
            {
                InternalLogger.Error("ExecutePromptAsync: requestJson is null or empty");
                return;
            }

            ExecutePromptRequest request;
            try
            {
                request = requestJson.FromJson<ExecutePromptRequest>();
                if (request == null)
                {
                    InternalLogger.Error("ExecutePromptAsync: deserialized request is null");
                    return;
                }
            }
            catch (Exception ex)
            {
                InternalLogger.Error("ExecutePromptAsync: failed to deserialize incoming request JSON.", ex);
                return;
            }

            try
            {
                const int MaxServerImages = 3;
                const long MaxServerImageBase64Chars = 6L * 1024 * 1024; // ~4 MB file

                bool hasImages = request.Images != null && request.Images.Count > 0;
                if (hasImages &&
                    (request.Images.Count > MaxServerImages ||
                     request.Images.Any(i => string.IsNullOrWhiteSpace(i)
                         || i.Length > MaxServerImageBase64Chars
                         || !i.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))))
                {
                    InternalLogger.Warn("ExecutePromptAsync: invalid image payload rejected");
                    return;
                }

                InternalLogger.Info("ExecutePromptAsync: request received. PromptLength=" +
                    (request.Prompt == null ? 0 : request.Prompt.Length) +
                    ", ImageCount=" + (request.Images == null ? 0 : request.Images.Count) +
                    ", DocumentCount=" + (request.Documents == null ? 0 : request.Documents.Count));

                const int MaxDocuments = 5;
                const int MaxDocumentBase64Chars = 14 * 1024 * 1024;
                var documentText = new List<string>();
                if (request.Documents != null)
                {
                    if (request.Documents.Count > MaxDocuments)
                    {
                        InternalLogger.Warn("ExecutePromptAsync: too many document attachments. Count=" + request.Documents.Count);
                        return;
                    }

                    foreach (var document in request.Documents)
                    {
                        if (document == null || string.IsNullOrWhiteSpace(document.Name)
                            || string.IsNullOrWhiteSpace(document.Data)
                            || document.Data.Length > MaxDocumentBase64Chars)
                        {
                            InternalLogger.Warn("ExecutePromptAsync: invalid document attachment rejected. Name=" +
                                (document == null ? "<null>" : document.Name) +
                                ", Base64Length=" + (document == null || document.Data == null ? 0 : document.Data.Length));
                            return;
                        }

                        InternalLogger.Info("Document extraction started. Name=" + document.Name +
                            ", MimeType=" + (document.MimeType ?? "<unknown>") +
                            ", Base64Length=" + document.Data.Length);
                        try
                        {
                            var extracted = DocumentTextExtractor.Extract(document.Name, document.Data);
                            InternalLogger.Info("Document extraction completed. Name=" + document.Name +
                                ", ExtractedTextLength=" + (extracted == null ? 0 : extracted.Length));
                            documentText.Add("## Attached file: " + document.Name + "\n" + (extracted ?? string.Empty));
                        }
                        catch (Exception documentError)
                        {
                            InternalLogger.Error("Document extraction failed. Name=" + document.Name, documentError);
                            documentText.Add("## Attached file: " + document.Name + "\n[Unable to extract document text: " + documentError.Message + "]");
                        }
                    }
                }

                var prompt = request.Prompt ?? string.Empty;
                if (documentText.Count > 0)
                    prompt += "\n\n" + string.Join("\n\n", documentText);

                var context = new GenerateStreamContext
                {
                    Prompt = prompt,
                    ActiveDocumentContent = request.IncludeContent ? await GetIncludedContentAsync() : null,
                    AdditionalPrompt = request.AdditionalPrompt,
                    ModelId = request.ModelId,
                    Temperature = request.Temperature,
                    Images = request.Images
                };

                async Task OnMessageAsync(WebView2ScriptMessage message)
                {
                    await _scriptExecutor.PostMessageAsJsonAsync(message).ConfigureAwait(false);
                }

                if (!await _sessionManager.TryStartSessionAsync(
                    context,
                    OnMessageAsync,
                    cancellationToken: CancellationToken.None).ConfigureAwait(false))
                {
                    InternalLogger.Info("ExecutePromptAsync: Session already running");
                }
            }
            catch (Exception ex)
            {
                InternalLogger.Error("ExecutePromptAsync failed", ex);
            }
        }

        /// <summary>
        /// Reads the active document and formats it as a Markdown fenced code block. 
        /// </summary>
        private async Task<string> GetIncludedContentAsync()
        {
            var doc = await _activeDocumentTool.GetActiveDocumentInfoAsync();
            if (doc?.Success != true || string.IsNullOrWhiteSpace(doc.Content))
                return null;

            return MarkdownCodeBlockFormatter.FormatFileAsMarkdown(doc.Content, doc.FilePath);
        }


        /// <summary>
        /// Resets the chat history with the specified action.
        /// </summary>
        public Task<bool> ResetHistoryWithActionAsync(string action)
        {
            return _chatHistoryService.ResetHistoryAsync(action);
        }

        /// <summary>
        /// Summarizes the current chat history via LLM, then replaces it with a compact user instruction + summary assistant pair. 
        /// </summary>
        public Task<bool> SummarizeAndCompactAsync(string modelId)
        {
            return _chatHistoryService.SummarizeAndCompactAsync(modelId);
        }

        /// <summary>
        /// Stops the current text generation process and active tools.
        /// </summary>
        public Task StopExecutionAsync()
        {
            InternalLogger.Info("StopExecutionAsync called");
            _sessionManager.TryStopSession();
            return Task.CompletedTask;
        }

        /// <summary>
        /// Loads the snapshot of changed files.
        /// </summary>
        public async Task<bool> GetSnapshotAsync()
        {
            try
            {
                await _snapshotManager.LoadSnapshotAsync().ConfigureAwait(false);
                return true;
            }
            catch (Exception ex)
            {
                InternalLogger.Error("GetSnapshotAsync failed", ex);
                return false;
            }
        }

        /// <summary>
        /// Discards (rolls back) all accepted file changes.
        /// </summary>
        public async Task<bool> DiscardChangesAsync()
        {
            try
            {
                await _snapshotManager.RollbackAllAsync().ConfigureAwait(false);
                return true;
            }
            catch (Exception ex)
            {
                InternalLogger.Error("DiscardChangesAsync failed", ex);
                return false;
            }
        }

        /// <summary>
        /// Accepts (commits) all file changes.
        /// </summary>
        public async Task<bool> AcceptChangesAsync()
        {
            try
            {
                await _snapshotManager.CommitAllAsync().ConfigureAwait(false);
                return true;
            }
            catch (Exception ex)
            {
                InternalLogger.Error("AcceptChangesAsync failed", ex);
                return false;
            }
        }

        /// <summary>
        /// Opens a diff viewer for a single changed file.
        /// </summary>
        public async Task<bool> ReviewFileAsync(string filePath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(filePath))
                    return false;

                var leftPath = await _snapshotManager.GetSnapshotFilePathAsync(filePath).ConfigureAwait(false);
                var rightPath = _snapshotManager.GetCurrentFilePath(filePath);
                var tmpDirectory = _snapshotManager.GetTmpDirectoryPath();

                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                await DiffViewer.ShowDiffAsync(leftPath, rightPath, tmpDirectory).ConfigureAwait(false);

                return true;
            }
            catch (Exception ex)
            {
                InternalLogger.Error("ReviewFileDiffAsync failed", ex);
                return false;
            }
        }

        /// <summary>
        /// Opens diff viewers for all changed files in the JSON list.
        /// </summary>
        public async Task<bool> ReviewAllFilesAsync(string filePathsJson)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(filePathsJson))
                    return false;

                var filePaths = JsonConvert.DeserializeObject<string[]>(filePathsJson);
                if (filePaths == null || filePaths.Length == 0)
                    return false;

                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                var tmpDirectory = _snapshotManager.GetTmpDirectoryPath();

                foreach (var relativePath in filePaths)
                {
                    if (string.IsNullOrWhiteSpace(relativePath))
                        continue;

                    var leftPath = await _snapshotManager.GetSnapshotFilePathAsync(relativePath).ConfigureAwait(false);
                    var rightPath = _snapshotManager.GetCurrentFilePath(relativePath);

                    await DiffViewer.ShowDiffAsync(leftPath, rightPath, tmpDirectory).ConfigureAwait(false);
                }

                return true;
            }
            catch (Exception ex)
            {
                InternalLogger.Error("ReviewAllFilesAsync failed", ex);
                return false;
            }
        }

        /// <summary>
        /// Opens all changed files from the JSON list in the editor.
        /// </summary>
        public async Task<bool> OpenAllFilesAsync(string filePathsJson)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(filePathsJson))
                    return false;

                var filePaths = JsonConvert.DeserializeObject<string[]>(filePathsJson);
                if (filePaths == null || filePaths.Length == 0)
                    return false;

                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                foreach (var relativePath in filePaths)
                {
                    if (string.IsNullOrWhiteSpace(relativePath))
                        continue;

                    var absolutePath = _snapshotManager.GetCurrentFilePath(relativePath);
                    if (absolutePath == null)
                        continue;

                    await FileViewer.OpenFileAsync(absolutePath).ConfigureAwait(false);
                }

                return true;
            }
            catch (Exception ex)
            {
                InternalLogger.Error("OpenAllFilesAsync failed", ex);
                return false;
            }
        }

        /// <summary>
        /// Discards (rolls back) a single changed file.
        /// </summary>
        public async Task<bool> DiscardFileAsync(string filePath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(filePath))
                    return false;

                await _snapshotManager.RollbackFilesAsync(new[] { filePath }).ConfigureAwait(false);

                return true;
            }
            catch (Exception ex)
            {
                InternalLogger.Error("DiscardFileAsync failed", ex);
                return false;
            }
        }

        /// <summary>
        /// Accepts (commits) a single changed file.
        /// </summary>
        public async Task<bool> AcceptFileAsync(string filePath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(filePath))
                    return false;

                await _snapshotManager.CommitFilesAsync(new[] { filePath }).ConfigureAwait(false);

                return true;
            }
            catch (Exception ex)
            {
                InternalLogger.Error("AcceptFileAsync failed", ex);
                return false;
            }
        }

        private async Task OnSnapshotChangedAsync(IReadOnlyList<SnapshotFileChange> changedFiles)
        {
            var message = new WebView2SnapshotMessage
            {
                ChangedFiles = changedFiles.ToList(),
            };
            await _scriptExecutor.PostMessageAsJsonAsync(message).ConfigureAwait(false);
        }
    }
}
