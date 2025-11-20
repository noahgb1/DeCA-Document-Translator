﻿using Azure.AI.Translation.Document;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Sas;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DocumentTranslationService.Core
{
    public partial class DocumentTranslationBusiness
    {
        #region Properties
        public DocumentTranslationService TranslationService { get; }

        /// <summary>Final target folder where translated files were placed.</summary>
        public string TargetFolder { get; private set; }

        /// <summary>Prevent deletion of storage containers (debugging).</summary>
        public bool Nodelete { get; set; } = false;

        /// <summary>Indicates overall run success (at least one translated file downloaded).</summary>
        public bool LastRunSuccessful { get; private set; }

        /// <summary>Reason for failure or early termination (null if successful).</summary>
        public string? LastRunFailureReason { get; private set; }

        public event EventHandler<string> OnThereWereErrors;
        public event EventHandler<long> OnFinalResults;
        public event EventHandler<StatusResponse> OnStatusUpdate;
        public event EventHandler<(int, long)> OnDownloadComplete;
        public event EventHandler OnUploadStart;
        public event EventHandler<(int, long)> OnUploadComplete;
        public event EventHandler<List<string>> OnFilesDiscarded;
        public event EventHandler<List<string>> OnGlossariesDiscarded;
        public event EventHandler<string> OnContainerCreationFailure;
        public event EventHandler<string> OnFileReadWriteError;
        public event EventHandler<int> OnHeartBeat;

        private readonly Logger logger = new();
        private Glossary glossary;
        #endregion Properties

        public DocumentTranslationBusiness(DocumentTranslationService documentTranslationService)
        {
            TranslationService = documentTranslationService;
        }

        /// <summary>
        /// Perform a translation of a set of files.
        /// Sets LastRunSuccessful / LastRunFailureReason instead of throwing for most early exits.
        /// </summary>
        public async Task RunAsync(List<string> filestotranslate,
                                   string fromlanguage,
                                   string[] tolanguages,
                                   List<string> glossaryfiles = null,
                                   string targetFolder = null)
        {
            LastRunSuccessful = false;
            LastRunFailureReason = null;

            Stopwatch stopwatch = new();
            stopwatch.Start();
            logger.WriteLine($"{stopwatch.Elapsed.TotalSeconds} Translation run started");

            if (filestotranslate is null || filestotranslate.Count == 0)
            {
                LastRunFailureReason = "No files to translate.";
                return;
            }

            Task initialize = TranslationService.InitializeAsync();

            #region Build source file list
            List<string> sourcefiles = new();
            foreach (string filename in filestotranslate)
            {
                try
                {
                    if ((File.GetAttributes(filename) & FileAttributes.Directory) == FileAttributes.Directory)
                    {
                        foreach (var file in Directory.EnumerateFiles(filename))
                            sourcefiles.Add(file);
                    }
                    else
                    {
                        sourcefiles.Add(filename);
                    }
                }
                catch (Exception ex)
                {
                    LastRunFailureReason = $"Accessing file or directory failed: {filename} - {ex.Message}";
                    return;
                }
            }
            try
            {
                await initialize;
            }
            catch (Exception ex)
            {
                LastRunFailureReason = $"Initialization failed: {ex.Message}";
                return;
            }
            #endregion

            #region Parameter checking
            if (TranslationService.Extensions == null || TranslationService.Extensions.Count == 0)
            {
                LastRunFailureReason = "List of translatable extensions is empty.";
                return;
            }

            List<string> discards;
            (sourcefiles, discards) = FilterByExtension(sourcefiles, TranslationService.Extensions);

            if (discards is not null && discards.Count > 0)
            {
                foreach (string fileName in discards)
                    logger.WriteLine($"Discarded due to invalid file format: {fileName}");
                OnFilesDiscarded?.Invoke(this, discards);
            }

            if (sourcefiles.Count == 0)
            {
                LastRunFailureReason = "All files were discarded (unsupported extensions).";
                return;
            }

            if (tolanguages == null || tolanguages.Length == 0)
            {
                LastRunFailureReason = "No target language provided.";
                return;
            }

            if (!TranslationService.Languages.ContainsKey(tolanguages[0]))
            {
                LastRunFailureReason = $"Invalid target language '{tolanguages[0]}'.";
                return;
            }
            #endregion

            List<string> sourcefiles_prep;
            try
            {
                sourcefiles_prep = LocalFormats.LocalFormats.PreprocessSourceFiles(sourcefiles);
            }
            catch (Exception ex)
            {
                LastRunFailureReason = $"Preprocessing failed: {ex.Message}";
                return;
            }

            #region Create containers
            logger.WriteLine($"{stopwatch.Elapsed.TotalSeconds} START - container creation.");
            string containerNameBase = "doctr" + Guid.NewGuid().ToString();
            BlobContainerClient sourceContainer;
            try
            {
                sourceContainer = new BlobContainerClient(TranslationService.StorageConnectionString, containerNameBase + "src");
            }
            catch (Exception ex)
            {
                logger.WriteLine(ex.Message + ex.InnerException?.Message);
                OnContainerCreationFailure?.Invoke(this, ex.Message);
                LastRunFailureReason = $"Source container client creation failed: {ex.Message}";
                return;
            }

            var sourceContainerTask = sourceContainer.CreateIfNotExistsAsync();
            TranslationService.ContainerClientSource = sourceContainer;

            List<Task> targetContainerTasks = new();
            Dictionary<string, BlobContainerClient> targetContainers = new();
            TranslationService.ContainerClientTargets.Clear();

            foreach (string lang in tolanguages)
            {
                try
                {
                    BlobContainerClient targetContainer =
                        new BlobContainerClient(TranslationService.StorageConnectionString, containerNameBase + "tgt" + lang.ToLowerInvariant());
                    targetContainerTasks.Add(targetContainer.CreateIfNotExistsAsync());
                    TranslationService.ContainerClientTargets.Add(lang, targetContainer);
                    targetContainers.Add(lang, targetContainer);
                }
                catch (Exception ex)
                {
                    LastRunFailureReason = $"Target container creation failed for {lang}: {ex.Message}";
                    OnFileReadWriteError?.Invoke(this, ex.Message);
                    return;
                }
            }
            #endregion

            #region Upload documents
            try
            {
                await sourceContainerTask;
            }
            catch (Exception ex)
            {
                logger.WriteLine(ex.Message + ex.InnerException?.Message);
                OnContainerCreationFailure?.Invoke(this, ex.Message);
                LastRunFailureReason = $"Source container creation failed: {ex.Message}";
                return;
            }

            logger.WriteLine($"{stopwatch.Elapsed.TotalSeconds} END - container creation.");
            logger.WriteLine($"{stopwatch.Elapsed.TotalSeconds} START - Documents & glossaries upload.");
            OnUploadStart?.Invoke(this, EventArgs.Empty);

            int uploadedCount = 0;
            long uploadedBytes = 0;
            List<Task> uploadTasks = new();

            using (System.Threading.SemaphoreSlim semaphore = new(50))
            {
                foreach (var filename in sourcefiles_prep)
                {
                    await semaphore.WaitAsync();
                    BlobClient blobClient = new BlobClient(TranslationService.StorageConnectionString,
                                                           TranslationService.ContainerClientSource.Name,
                                                           Normalize(filename));
                    try
                    {
                        uploadTasks.Add(blobClient.UploadAsync(filename, overwrite: true));
                        uploadedCount++;
                        uploadedBytes += new FileInfo(filename).Length;
                    }
                    catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or AggregateException or Azure.RequestFailedException)
                    {
                        logger.WriteLine($"Uploading file {filename} failed with {ex.Message}");
                        OnFileReadWriteError?.Invoke(this, ex.Message);
                        LastRunFailureReason = $"File upload failed: {filename} - {ex.Message}";
                        if (!Nodelete) await DeleteContainersAsync(tolanguages);
                        semaphore.Release();
                        return;
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                    logger.WriteLine($"File {filename} upload scheduled.");
                }
            }

            try
            {
                await Task.WhenAll(uploadTasks);
            }
            catch (Exception ex)
            {
                logger.WriteLine($"Uploading files failed with {ex.Message}");
                OnFileReadWriteError?.Invoke(this, ex.Message);
                LastRunFailureReason = $"Batch upload failed: {ex.Message}";
                if (!Nodelete) await DeleteContainersAsync(tolanguages);
                return;
            }

            glossary = new Glossary(TranslationService, glossaryfiles);
            glossary.OnGlossaryDiscarded += Glossary_OnGlossaryDiscarded;
            try
            {
                if (glossaryfiles != null && glossaryfiles.Count > 0)
                    await glossary.UploadAsync(TranslationService.StorageConnectionString, containerNameBase);
            }
            catch (Exception ex)
            {
                logger.WriteLine($"Glossaries upload failed with {ex.Message}");
                OnFileReadWriteError?.Invoke(this, ex.Message);
                LastRunFailureReason = $"Glossary upload failed: {ex.Message}";
                if (!Nodelete) await DeleteContainersAsync(tolanguages);
                return;
            }

            OnUploadComplete?.Invoke(this, (uploadedCount, uploadedBytes));
            logger.WriteLine($"{stopwatch.Elapsed.TotalSeconds} END - Document upload. {uploadedBytes} bytes in {uploadedCount} documents.");
            #endregion

            #region Translate
            try
            {
                await Task.WhenAll(targetContainerTasks);
            }
            catch (Exception ex)
            {
                logger.WriteLine($"Target container creation failed with {ex.Message}");
                OnFileReadWriteError?.Invoke(this, ex.Message);
                LastRunFailureReason = $"Target container creation failed: {ex.Message}";
                if (!Nodelete) await DeleteContainersAsync(tolanguages);
                return;
            }

            try
            {
                DocumentTranslationInput input = GenerateInput(fromlanguage, tolanguages, sourceContainer, targetContainers, glossary, false);
                string statusID = await TranslationService.SubmitTranslationRequestAsync(input);
                logger.WriteLine($"{stopwatch.Elapsed.TotalSeconds} START - Translation request (SAS). StatusID: {statusID}");
            }
            catch (Azure.RequestFailedException ex)
            {
                OnStatusUpdate?.Invoke(this, new StatusResponse(TranslationService.DocumentTranslationOperation, ex.ErrorCode + ": " + ex.Message));
                logger.WriteLine($"Azure Request Failed - {ex.ErrorCode}: {ex.Message}");
                OnThereWereErrors?.Invoke(this, $"Azure Request Failed - {ex.ErrorCode}: {ex.Message}");
                LastRunFailureReason = $"Azure request failed: {ex.ErrorCode} {ex.Message}";
                if (!Nodelete) await DeleteContainersAsync(tolanguages);
                return;
            }
            catch (Exception ex)
            {
                OnStatusUpdate?.Invoke(this, new StatusResponse(TranslationService.DocumentTranslationOperation, ex.Message));
                logger.WriteLine($"Translation submission failed: {ex.Message}");
                OnThereWereErrors?.Invoke(this, $"Translation submission failed: {ex.Message}");
                LastRunFailureReason = $"Submission failed: {ex.Message}";
                if (!Nodelete) await DeleteContainersAsync(tolanguages);
                return;
            }

            if (TranslationService.DocumentTranslationOperation is null)
            {
                logger.WriteLine("ERROR: Translation operation null.");
                OnThereWereErrors?.Invoke(this, "Translation operation null after submission.");
                LastRunFailureReason = "Translation operation null.";
                if (!Nodelete) await DeleteContainersAsync(tolanguages);
                return;
            }

            DateTimeOffset lastActionTime = DateTimeOffset.MinValue;
            DocumentTranslationOperation status;
            do
            {
                await Task.Delay(1000);
                status = null;
                try
                {
                    status = await TranslationService.CheckStatusAsync();
                }
                catch (Azure.RequestFailedException ex)
                {
                    if (ex.ErrorCode == "InvalidRequest")
                    {
                        try
                        {
                            DocumentTranslationInput inputMI = GenerateInput(fromlanguage, tolanguages, sourceContainer, targetContainers, glossary, true);
                            string statusID = await TranslationService.SubmitTranslationRequestAsync(inputMI);
                            logger.WriteLine($"{stopwatch.Elapsed.TotalSeconds} RETRY - Translation request (Managed Identity). StatusID: {statusID}");
                            await Task.Delay(300);
                            status = await TranslationService.CheckStatusAsync();
                        }
                        catch (Azure.RequestFailedException ex2)
                        {
                            OnStatusUpdate?.Invoke(this, new StatusResponse(TranslationService.DocumentTranslationOperation, ex2.ErrorCode + ": " + ex2.Message));
                            logger.WriteLine("Retry with MI failed: " + ex2.ErrorCode + ": " + ex2.Message);
                            OnThereWereErrors?.Invoke(this, ex2.ErrorCode + "  " + ex2.Message);
                            LastRunFailureReason = $"Retry failed: {ex2.ErrorCode} {ex2.Message}";
                            return;
                        }
                    }
                    else
                    {
                        OnStatusUpdate?.Invoke(this, new StatusResponse(TranslationService.DocumentTranslationOperation, ex.ErrorCode + ": " + ex.Message));
                        logger.WriteLine(ex.ErrorCode + ": " + ex.Message);
                        OnThereWereErrors?.Invoke(this, ex.ErrorCode + "  " + ex.Message);
                        LastRunFailureReason = $"Status check failed: {ex.ErrorCode} {ex.Message}";
                        return;
                    }
                }
                catch (Exception ex)
                {
                    OnThereWereErrors?.Invoke(this, ex.Message);
                    logger.WriteLine($"{stopwatch.Elapsed.TotalSeconds} Status error: {ex.Message}");
                    LastRunFailureReason = $"Status error: {ex.Message}";
                    return;
                }

                if (status is null)
                {
                    LastRunFailureReason = "Status returned null.";
                    OnThereWereErrors?.Invoke(this, "Status returned null.");
                    return;
                }

                logger.WriteLine($"{stopwatch.Elapsed.TotalSeconds} Http status: {TranslationService.AzureHttpStatus.Status} {TranslationService.AzureHttpStatus.ReasonPhrase}");
                OnHeartBeat?.Invoke(this, TranslationService.AzureHttpStatus.Status);

                logger.WriteLine($"{stopwatch.Elapsed.TotalSeconds} Service status: {status.CreatedOn} {status.Status}");
                if (status.LastModified != lastActionTime)
                {
                    OnStatusUpdate?.Invoke(this, new StatusResponse(status));
                    lastActionTime = status.LastModified;
                }

                if (status.Status == DocumentTranslationStatus.ValidationFailed)
                {
                    LastRunFailureReason = "Validation failed.";
                    OnThereWereErrors?.Invoke(this, "Validation failed.");
                    if (!Nodelete) await DeleteContainersAsync(tolanguages);
                    return;
                }
            }
            while (status.DocumentsInProgress != 0 || !status.HasCompleted);

            OnStatusUpdate?.Invoke(this, new StatusResponse(status));
            Task<List<DocumentStatusResult>> finalResultsTask = TranslationService.GetFinalResultsAsync();
            #endregion

            #region Download
            logger.WriteLine($"{stopwatch.Elapsed.TotalSeconds} START - document download.");
            int downloadedCount = 0;
            long downloadedBytes = 0;

            foreach (string lang in tolanguages)
            {
                string directoryName;
                if (string.IsNullOrEmpty(targetFolder))
                    directoryName = Path.GetDirectoryName(sourcefiles[0]) + Path.DirectorySeparatorChar + lang;
                else if (targetFolder.Contains('*'))
                    directoryName = targetFolder.Replace("*", lang);
                else if (tolanguages.Length == 1)
                    directoryName = targetFolder;
                else
                    directoryName = targetFolder + Path.DirectorySeparatorChar + lang;

                DirectoryInfo directory;
                try
                {
                    directory = Directory.CreateDirectory(directoryName);
                }
                catch (UnauthorizedAccessException ex)
                {
                    logger.WriteLine(ex.Message);
                    OnFileReadWriteError?.Invoke(this, ex.Message);
                    LastRunFailureReason = $"Unauthorized creating output directory: {directoryName}";
                    if (!Nodelete) await DeleteContainersAsync(tolanguages);
                    return;
                }

                List<Task> downloads = new();
                using (System.Threading.SemaphoreSlim semaphore = new(50))
                {
                    await foreach (var blobItem in TranslationService.ContainerClientTargets[lang].GetBlobsAsync())
                    {
                        await semaphore.WaitAsync();
                        downloads.Add(DownloadBlobAsync(directory, blobItem, lang));
                        downloadedCount++;
                        downloadedBytes += (long)(blobItem.Properties.ContentLength ?? 0);
                        semaphore.Release();
                    }
                }

                try
                {
                    await Task.WhenAll(downloads);
                }
                catch (Exception ex)
                {
                    logger.WriteLine("Download error: " + ex.Message);
                    OnFileReadWriteError?.Invoke(this, "Download failure: " + ex.Message);
                    LastRunFailureReason = $"Download failure: {ex.Message}";
                }

                TargetFolder = directoryName;
            }

            try
            {
                if (Directory.Exists(TargetFolder))
                {
                    LocalFormats.LocalFormats.PostprocessTargetFiles(Directory.GetFiles(TargetFolder).ToList());
                }
            }
            catch (Exception ex)
            {
                logger.WriteLine("Postprocessing error: " + ex.Message);
                OnFileReadWriteError?.Invoke(this, "Postprocessing failure: " + ex.Message);
            }
            #endregion

            #region Finalization
            OnDownloadComplete?.Invoke(this, (downloadedCount, downloadedBytes));
            logger.WriteLine($"{stopwatch.Elapsed.TotalSeconds} END - Documents downloaded: {downloadedBytes} bytes in {downloadedCount} files.");

            var finalResults = await finalResultsTask;
            OnFinalResults?.Invoke(this, CharactersCharged(finalResults));

            StringBuilder sb = new();
            bool thereWereErrors = false;
            foreach (var documentStatus in finalResults)
            {
                if (documentStatus.Error is not null)
                {
                    thereWereErrors = true;
                    sb.Append(ToDisplayForm(documentStatus.SourceDocumentUri.LocalPath)).Append('\t');
                    sb.Append(documentStatus.TranslatedToLanguageCode).Append('\t');
                    sb.Append(documentStatus.Error.Message);
                    sb.AppendLine(" (" + documentStatus.Error.Code + ")");
                }
            }
            if (thereWereErrors)
            {
                OnThereWereErrors?.Invoke(this, sb.ToString());
            }

            // If no files downloaded and no explicit failure reason, set a diagnostic reason
            if (downloadedCount == 0 && LastRunFailureReason == null)
            {
                LastRunFailureReason = "No translated documents were produced (zero blobs in target containers).";
            }

            LastRunSuccessful = downloadedCount > 0 && LastRunFailureReason == null;

            if (!Nodelete)
                await DeleteContainersAsync(tolanguages);

            logger.WriteLine($"{stopwatch.Elapsed.TotalSeconds} Run: Exiting. Success={LastRunSuccessful} FailureReason={LastRunFailureReason}");
            logger.Close();
            #endregion
        }

        private void Glossary_OnGlossaryDiscarded(object sender, List<string> e)
        {
            OnGlossariesDiscarded?.Invoke(this, e);
        }

        private DocumentTranslationInput GenerateInput(string fromlanguage,
                                                       string[] tolanguages,
                                                       BlobContainerClient sourceContainer,
                                                       Dictionary<string, BlobContainerClient> targetContainers,
                                                       Glossary glossary,
                                                       bool UseManagedIdentity)
        {
            Uri sourceUri = GenerateSasUriSource(sourceContainer, UseManagedIdentity);
            TranslationSource translationSource = new(sourceUri);
            logger.WriteLine($"UseManagedIdentity: {UseManagedIdentity}");
            logger.WriteLine($"SourceURI: {sourceUri}");

            if (!string.IsNullOrEmpty(fromlanguage))
            {
                if (fromlanguage.Equals("auto", StringComparison.OrdinalIgnoreCase))
                    translationSource.LanguageCode = null;
                else
                    translationSource.LanguageCode = fromlanguage;
            }

            Dictionary<string, Uri> sasUriTargets = GenerateSasUriTargets(tolanguages, targetContainers, UseManagedIdentity);
            List<TranslationTarget> translationTargets = new();
            logger.WriteLine($"Category value: '{TranslationService.Category}' (Length: {TranslationService.Category?.Length ?? -1})");

            foreach (string lang in tolanguages)
            {
                TranslationTarget translationTarget = new(sasUriTargets[lang], lang);
                if (glossary?.Glossaries is not null)
                {
                    if (UseManagedIdentity)
                        foreach (var glos in glossary.PlainUriGlossaries) translationTarget.Glossaries.Add(glos.Value);
                    else
                        foreach (var glos in glossary.Glossaries) translationTarget.Glossaries.Add(glos.Value);
                }

                if (!string.IsNullOrEmpty(TranslationService.Category))
                {
                    logger.WriteLine($"Setting CategoryId to: '{TranslationService.Category}'");
                    translationTarget.CategoryId = TranslationService.Category;
                }
                else
                {
                    logger.WriteLine("Category is null or empty - not setting CategoryId");
                }

                translationTargets.Add(translationTarget);
            }

            return new DocumentTranslationInput(translationSource, translationTargets);
        }

        private Dictionary<string, Uri> GenerateSasUriTargets(string[] tolanguages,
                                                              Dictionary<string, BlobContainerClient> targetContainers,
                                                              bool UseManagedIdentity)
        {
            Dictionary<string, Uri> sasUriTargets = new();
            foreach (string lang in tolanguages)
            {
                if (UseManagedIdentity)
                    sasUriTargets.Add(lang, targetContainers[lang].Uri);
                else
                    sasUriTargets.Add(lang, targetContainers[lang].GenerateSasUri(
                        BlobContainerSasPermissions.Write | BlobContainerSasPermissions.List,
                        DateTimeOffset.UtcNow + TimeSpan.FromHours(5)));

                logger.WriteLine($"TargetURI: {sasUriTargets[lang]}");
            }
            return sasUriTargets;
        }

        private static Uri GenerateSasUriSource(BlobContainerClient sourceContainer, bool UseManagedIdentity)
        {
            return UseManagedIdentity
                ? sourceContainer.Uri
                : sourceContainer.GenerateSasUri(BlobContainerSasPermissions.Read | BlobContainerSasPermissions.List,
                                                 DateTimeOffset.UtcNow + TimeSpan.FromHours(5));
        }

        private static string ToDisplayForm(string localPath)
        {
            string[] splits = localPath.Split('/');
            return splits[^1];
        }

        private long CharactersCharged(List<DocumentStatusResult> finalResults)
        {
            long characterscharged = 0;
            foreach (var result in finalResults)
                characterscharged += result.CharactersCharged;

            logger.WriteLine($"Total characters charged: {characterscharged}");
            return characterscharged;
        }

        private async Task DownloadBlobAsync(DirectoryInfo directory, BlobItem blobItem, string tolanguage)
        {
            BlobClient blobClient = new BlobClient(TranslationService.StorageConnectionString,
                                                   TranslationService.ContainerClientTargets[tolanguage].Name,
                                                   blobItem.Name);
            BlobDownloadInfo blobDownloadInfo = await blobClient.DownloadAsync();
            FileStream downloadFileStream;
            try
            {
                downloadFileStream = File.Create(Path.Combine(directory.FullName, blobItem.Name));
            }
            catch (IOException)
            {
                downloadFileStream = File.Create(Path.Combine(directory.FullName,
                                     Path.GetFileNameWithoutExtension(blobItem.Name) + "." + tolanguage + Path.GetExtension(blobItem.Name)));
            }

            await blobDownloadInfo.Content.CopyToAsync(downloadFileStream);
            downloadFileStream.Close();
            logger.WriteLine("Downloaded: " + downloadFileStream.Name);
        }

        public async Task<int> ClearOldContainersAsync()
        {
            logger.WriteLine("START - Abandoned containers deletion.");
            int counter = 0;
            List<Task> deletionTasks = new();
            BlobServiceClient blobServiceClient = new(TranslationService.StorageConnectionString);
            var resultSegment = blobServiceClient.GetBlobContainersAsync(BlobContainerTraits.None, BlobContainerStates.None, "doctr").AsPages();

            await foreach (Azure.Page<BlobContainerItem> containerPage in resultSegment)
            {
                foreach (var containerItem in containerPage.Values)
                {
                    BlobContainerClient client = new(TranslationService.StorageConnectionString, containerItem.Name);
                    if (containerItem.Name.EndsWith("src") ||
                        containerItem.Name.Contains("tgt") ||
                        containerItem.Name.Contains("gls"))
                    {
                        if (containerItem.Properties.LastModified < (DateTimeOffset.UtcNow - TimeSpan.FromDays(7)))
                        {
                            deletionTasks.Add(client.DeleteAsync());
                            counter++;
                        }
                    }
                }
            }

            await Task.WhenAll(deletionTasks);
            logger.WriteLine($"END - Abandoned containers deleted: {counter}");
            return counter;
        }

        private async Task DeleteContainersAsync(string[] tolanguages)
        {
            logger.WriteLine("START - Container deletion.");
            List<Task> deletionTasks = new();

            if (TranslationService?.ContainerClientSource is not null)
                deletionTasks.Add(SafeDeleteAsync(TranslationService.ContainerClientSource));

            if (TranslationService?.ContainerClientTargets is not null && tolanguages != null)
            {
                foreach (string lang in tolanguages)
                {
                    if (TranslationService.ContainerClientTargets.TryGetValue(lang, out var tgtClient) && tgtClient is not null)
                        deletionTasks.Add(SafeDeleteAsync(tgtClient));
                }
            }

            if (glossary is not null)
            {
                try { deletionTasks.Add(glossary.DeleteAsync()); }
                catch (Exception ex) { logger.WriteLine("Glossary delete scheduling failed: " + ex.Message); }
            }

            if (DateTime.Now.Millisecond < 100)
            {
                try { deletionTasks.Add(ClearOldContainersAsync()); }
                catch (Exception ex) { logger.WriteLine("ClearOldContainersAsync scheduling failed: " + ex.Message); }
            }

            try { await Task.WhenAll(deletionTasks); }
            catch (Exception ex) { logger.WriteLine("Container deletion encountered errors: " + ex.Message); }
            logger.WriteLine("END - Containers deleted.");
        }

        private static async Task SafeDeleteAsync(BlobContainerClient client)
        {
            try { await client.DeleteAsync(); } catch { }
        }

        public static string Normalize(string filename) => Path.GetFileName(filename);

        public static (List<string>, List<string>) FilterByExtension(List<string> fileNames, HashSet<string> validExtensions)
        {
            if (fileNames is null) return (null, null);
            List<string> validNames = new();
            List<string> discardedNames = new();
            foreach (string filename in fileNames)
            {
                if (validExtensions.Contains(Path.GetExtension(filename).ToLowerInvariant()))
                    validNames.Add(filename);
                else
                    discardedNames.Add(filename);
            }
            return (validNames, discardedNames);
        }
    }
}