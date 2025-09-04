namespace DocumentTranslation.Web.Models
{
    public class DocumentTranslationOptions
    {
        public string AzureResourceName { get; set; } = string.Empty;
        public string SubscriptionKey { get; set; } = string.Empty;
        public string AzureRegion { get; set; } = string.Empty;
        public string TextTransEndpoint { get; set; } = "https://api.cognitive.microsofttranslator.com/";
        public bool ShowExperimental { get; set; } = false;
        public string Category { get; set; } = string.Empty;
        public ConnectionStrings ConnectionStrings { get; set; } = new();
    }

    public class ConnectionStrings
    {
        public string StorageConnectionString { get; set; } = string.Empty;
    }
}