using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace FreeFlow.Windows.Services;

/// <summary>Sends a WAV recording to an OpenAI-compatible /audio/transcriptions endpoint.</summary>
public static class TranscriptionService
{
    public static async Task<string> TranscribeAsync(byte[] wav, AppSettings settings, CancellationToken ct)
    {
        var url = ApiClient.NormalizeBaseUrl(settings.ApiBaseUrl) + "/audio/transcriptions";

        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(wav);
        file.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        form.Add(file, "file", "audio.wav");
        form.Add(new StringContent(string.IsNullOrWhiteSpace(settings.TranscriptionModel) ? "whisper-large-v3-turbo" : settings.TranscriptionModel.Trim()), "model");
        form.Add(new StringContent("json"), "response_format");
        form.Add(new StringContent("0"), "temperature");

        if (!string.IsNullOrWhiteSpace(settings.Language))
        {
            form.Add(new StringContent(settings.Language.Trim().ToLowerInvariant()), "language");
        }

        // Whisper's "prompt" nudges spelling of names and jargon.
        var vocabulary = settings.VocabularyTerms();
        if (vocabulary.Count > 0)
        {
            var prompt = string.Join(", ", vocabulary.Take(60));
            form.Add(new StringContent(prompt), "prompt");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = form };
        using var json = await ApiClient.SendAsync(request, settings.ApiKey, ct).ConfigureAwait(false);

        if (json.RootElement.ValueKind == JsonValueKind.Object &&
            json.RootElement.TryGetProperty("text", out var text) &&
            text.ValueKind == JsonValueKind.String)
        {
            return (text.GetString() ?? "").Trim();
        }
        throw new UserFacingException("The transcription response had no text.");
    }
}
