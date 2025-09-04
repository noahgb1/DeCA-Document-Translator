using Azure.AI.Translation.Document;
using Azure.Storage.Blobs;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DocumentTranslationService.Core
{
    public partial class DocumentTranslationService
    {
        #region Properties
        /// <summary>
        /// The "Connection String" of the Azure blob storage resource. Get from properties of Azure storage.
        /// </summary>
        public string StorageConnectionString { get; set; } = string.Empty;

        /// <summary>
        /// Holds the Custom Translator category.
        /// </summary>
        public string Category { get; set; }

        /// <summary>
        /// Your Azure Translator resource key. Get from properties of the Translator resource
        /// </summary>
        public string SubscriptionKey { get; set; } = string.Empty;

        /// <summary>
        /// The region of your Translator subscription.
        /// Needed only for text translation; can remain empty for document translation.
        /// </summary>
        public string AzureRegion { get; set; }

        /// <summary>
        /// The Uri of the Azure Document Translation endpoint
        /// </summary>
        public string AzureResourceName { get; set; } = string.Empty;

        /// <summary>
        /// The URI of the Azure Text Translation endpoint
        /// </summary>
        public string TextTransUri { get; set; } = string.Empty;

        /// <summary>
        /// Sets the string to be used as a flight
        /// </summary>
        public string FlightString { get; set; } = string.Empty;

        internal BlobContainerClient ContainerClientSource { get; set; }
        internal Dictionary<string, BlobContainerClient> ContainerClientTargets { get; set; } = new();

        /// <summary>
        /// Holds the Azure Http Status to check during the run of the translation 
        /// </summary>
        internal Azure.Response AzureHttpStatus;

        public DocumentTranslationOperation DocumentTranslationOperation { get => documentTranslationOperation; set => documentTranslationOperation = value; }

        private DocumentTranslationClient documentTranslationClient;

        private DocumentTranslationOperation documentTranslationOperation;

        private CancellationToken cancellationToken;

        private CancellationTokenSource cancellationTokenSource;


        #endregion Properties
        #region Constants
        /// <summary>
        /// The base URL template for making translation requests.
        /// </summary>
        private string GetBaseUriTemplate()
        {
            // Check if we're using Azure Government based on existing configuration
            if (!string.IsNullOrEmpty(TextTransUri) && TextTransUri.Contains(".azure.us"))
                return ".cognitiveservices.azure.us/";
            if (!string.IsNullOrEmpty(AzureResourceName) && AzureResourceName.Contains(".azure.us"))
                return ".cognitiveservices.azure.us/";
            
            // Default to public Azure
            return ".cognitiveservices.azure.com/";
        }
        #endregion Constants
        #region Methods

        /// <summary>
        /// Constructor
        /// </summary>
        public DocumentTranslationService(string SubscriptionKey, string AzureResourceName, string StorageConnectionString)
        {
            this.SubscriptionKey = SubscriptionKey;
            this.AzureResourceName = AzureResourceName;
            this.StorageConnectionString = StorageConnectionString;
        }

        public DocumentTranslationService()
        {

        }

        /// <summary>
        /// Fires when initialization is complete.
        /// </summary>
        public event EventHandler OnInitializeComplete;

        /// <summary>
        /// Fills the properties with values from the service. 
        /// </summary>
        /// <returns></returns>
        public async Task InitializeAsync()
        {
            if (string.IsNullOrEmpty(AzureResourceName)) throw new CredentialsException("name");
            if (string.IsNullOrEmpty(SubscriptionKey)) throw new CredentialsException("key");
            if (string.IsNullOrEmpty(TextTransUri)) TextTransUri = "https://api.cognitive.microsofttranslator.com/";
            
            string baseUriTemplate = GetBaseUriTemplate();
            string DocTransEndpoint;
            if (!AzureResourceName.Contains('.')) DocTransEndpoint = "https://" + AzureResourceName + baseUriTemplate;
            else DocTransEndpoint = AzureResourceName;
            
            // Add debug logging
            Console.WriteLine($"[DEBUG] InitializeAsync - AzureResourceName: {AzureResourceName}");
            Console.WriteLine($"[DEBUG] InitializeAsync - DocTransEndpoint: {DocTransEndpoint}");
            Console.WriteLine($"[DEBUG] InitializeAsync - baseUriTemplate: {baseUriTemplate}");
            
            var options = new DocumentTranslationClientOptions();
            if (!string.IsNullOrEmpty(FlightString)) options.AddPolicy(new FlightPolicy(FlightString.Trim()), Azure.Core.HttpPipelinePosition.PerCall);
            documentTranslationClient = new(new Uri(DocTransEndpoint), new Azure.AzureKeyCredential(SubscriptionKey), options);
            
            List<Task> tasks = new()
            {
                GetDocumentFormatsAsync(),
                GetGlossaryFormatsAsync()
            };
            try
            {
                await Task.WhenAll(tasks);
                await GetLanguagesAsync();
            }
            catch (CredentialsException ex)
            {
                throw new CredentialsException(ex.Message, ex);
            }
            OnInitializeComplete?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// Retrieve the status of the translation progress.
        /// </summary>
        /// <returns></returns>
        public async Task<DocumentTranslationOperation> CheckStatusAsync()
        {
            for (int i = 0; i < 3; i++)
            {
                AzureHttpStatus = await documentTranslationOperation.UpdateStatusAsync(cancellationToken);
                if (AzureHttpStatus.IsError)
                {
                    await Task.Delay(300);
                    continue;
                }
                return documentTranslationOperation;
            }
            return null;
        }

        /// <summary>
        /// Cancels an ongoing translation run. 
        /// </summary>
        /// <returns></returns>
        public async Task<Azure.Response> CancelRunAsync()
        {
            cancellationTokenSource.Cancel();
            await documentTranslationOperation.CancelAsync(cancellationToken);
            Azure.Response response = await documentTranslationOperation.UpdateStatusAsync(cancellationToken);
            Debug.WriteLine($"Cancellation: {response.Status} {response.ReasonPhrase}");
            return response;
        }


        /// <summary>
        /// Submit the translation request to the Document Translation Service. 
        /// </summary>
        /// <param name="input">An object defining the input of what to translate</param>
        /// <returns>The status ID</returns>
        public async Task<string> SubmitTranslationRequestAsync(DocumentTranslationInput input)
        {
            if (String.IsNullOrEmpty(AzureResourceName)) throw new CredentialsException("name");
            if (String.IsNullOrEmpty(SubscriptionKey)) throw new CredentialsException("key");
            if (String.IsNullOrEmpty(StorageConnectionString)) throw new CredentialsException("storage");
            
            cancellationTokenSource = new();
            cancellationToken = cancellationTokenSource.Token;
            try
            {
                // Log request details before submission
                Console.WriteLine($"[DEBUG] Submitting translation request to: {AzureResourceName}");
                Console.WriteLine($"[DEBUG] Using region: {AzureRegion}");
                Console.WriteLine($"[DEBUG] Source URI: {input.Source.SourceUri}");
                Console.WriteLine($"[DEBUG] Target count: {input.Targets?.Count ?? 0}");
                if (input.Targets != null && input.Targets.Count > 0)
                {
                    foreach (var target in input.Targets)
                    {
                        Console.WriteLine($"[DEBUG] Target URI: {target.TargetUri}, Language: {target.LanguageCode}");
                    }
                }
                
                documentTranslationOperation = await documentTranslationClient.StartTranslationAsync(input, cancellationToken);
                Console.WriteLine($"[DEBUG] Translation operation started successfully with ID: {documentTranslationOperation?.Id}");
            }
            catch (Azure.RequestFailedException ex)
            {
                Console.WriteLine($"[ERROR] Azure Request Failed - Error Code: {ex.ErrorCode}, Status: {ex.Status}");
                Console.WriteLine($"[ERROR] Message: {ex.Message}");
                Console.WriteLine($"[ERROR] Source: {ex.Source}");
                throw new Exception($"Azure Request Failed - Error Code: {ex.ErrorCode}, Status: {ex.Status}, Message: {ex.Message}");
            }
            catch (System.InvalidOperationException ex)
            {
                Console.WriteLine($"[ERROR] Invalid Operation - Source: {ex.Source}, Message: {ex.Message}");
                throw new Exception($"Invalid Operation - Source: {ex.Source}, Message: {ex.Message}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] Unexpected error during translation submission: {ex.GetType().Name} - {ex.Message}");
                throw new Exception($"Unexpected error during translation submission: {ex.GetType().Name} - {ex.Message}");
            }
            await documentTranslationOperation.UpdateStatusAsync();
            Debug.WriteLine("Translation Request submitted. Status: " + documentTranslationOperation.Status);
            return documentTranslationOperation.Id;
        }

        public async Task<List<DocumentStatusResult>> GetFinalResultsAsync()
        {
            List<DocumentStatusResult> documentStatuses = new();
            Debug.WriteLine("Final results:");
            await foreach (DocumentStatusResult document in documentTranslationOperation.GetValuesAsync(cancellationToken))
            {
                documentStatuses.Add(document);
                Debug.WriteLine($"{document.SourceDocumentUri}\t{document.Error}\t{document.Status}\t{document.TranslatedToLanguageCode}");
            }
            return documentStatuses;
        }

        #endregion Methods
    }
}

