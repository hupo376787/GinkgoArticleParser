using System.Text.Json.Serialization;

namespace GinkgoArticleParser.Models
{
    public class PicturePageInfoModel
    {
        [JsonPropertyName("cdn_url")]
        public string CdnUrl { get; set; }

        [JsonPropertyName("width")]
        public string Width { get; set; }

        [JsonPropertyName("height")]
        public string Height { get; set; }
    }
}
