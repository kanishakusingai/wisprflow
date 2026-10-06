using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace FreeFlow.Windows.Services;

/// <summary>An error with a short message that is safe to show in the Flow Bar.</summary>
public sealed class UserFacingException : Exception
{
    public UserFacingException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>Shared HTTP plumbing for the OpenAI-compatible API (Groq by default).</summary>
public static class ApiClient
{
    // One client for the app's lifetime; no cookies, no caching.
    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        UseCookies = false,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    })
    {
        Timeout = TimeSpan.FromSeconds(60),
    };

    public static string NormalizeBaseUrl(string baseUrl)
    {
        var trimmed = (baseUrl ?? "").Trim().TrimEnd('/');
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) ||
            string.IsNullOrEmpty(uri.Host))
        {
            throw new UserFacingException("The API base URL in Settings isn't a valid http(s) address.");
        }
        return trimmed;
    }

    public static async Task<JsonDocument> SendAsync(HttpRequestMessage request, string apiKey, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new UserFacingException("Add your API key in FreeFlow Settings first.");
        }
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        HttpResponseMessage response;
        try
        {
            response = await Http.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new UserFacingException("The request timed out. Check your connection and try again.", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new UserFacingException("Couldn't reach the server. Check your internet connection.", ex);
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new UserFacingException(DescribeFailure(response.StatusCode, body));
            }
            try
            {
                return JsonDocument.Parse(body);
            }
            catch (JsonException ex)
            {
                throw new UserFacingException("The server sent a response FreeFlow couldn't read.", ex);
            }
        }
    }

    private static string DescribeFailure(HttpStatusCode status, string body)
    {
        var detail = "";
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var err) &&
                err.ValueKind == JsonValueKind.Object &&
                err.TryGetProperty("message", out var msg))
            {
                detail = msg.GetString() ?? "";
            }
        }
        catch (JsonException)
        {
        }

        return (int)status switch
        {
            401 or 403 => "Your API key was rejected. Check it in Settings.",
            404 => "Model or endpoint not found. Check the models in Settings.",
            413 => "That recording is too long. Try a shorter one.",
            429 => "Rate limit reached. Wait a moment and try again.",
            >= 500 => "The transcription service is having problems. Try again shortly.",
            _ => string.IsNullOrWhiteSpace(detail) ? $"Request failed ({(int)status})." : detail,
        };
    }

    /// <summary>Checks the key and URL by listing models. Returns null on success, else an error.</summary>
    public static async Task<string?> TestConnectionAsync(string baseUrl, string apiKey, CancellationToken ct = default)
    {
        try
        {
            var url = NormalizeBaseUrl(baseUrl) + "/models";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using var _ = await SendAsync(request, apiKey, ct).ConfigureAwait(false);
            return null;
        }
        catch (UserFacingException ex)
        {
            return ex.Message;
        }
    }
}
