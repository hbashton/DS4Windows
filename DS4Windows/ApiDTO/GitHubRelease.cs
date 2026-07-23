using System.Text.Json.Serialization;

namespace DS4WinWPF.ApiDTO
{
    // https://docs.github.com/en/rest/releases/releases?apiVersion=2022-11-28
    public class GithubRelease
    {
        [JsonPropertyName("tag_name")] public string TagName { get; set; }
        [JsonPropertyName("prerelease")] public bool PreRelease { get; set; }
        [JsonPropertyName("draft")] public bool Draft { get; set; }
        [JsonPropertyName("published_at")] public System.DateTimeOffset? PublishedAt { get; set; }
        [JsonPropertyName("created_at")] public System.DateTimeOffset? CreatedAt { get; set; }
        [JsonPropertyName("body")] public string Body { get; set; }
    }
}
