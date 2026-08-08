using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace CaYaTunnel.Server.Dns;

/// <summary>
/// Creates the A/CNAME records that make hostname tunnels resolve, via the Cloudflare API.
/// <para>
/// The token needs exactly one permission: Zone / DNS / Edit on the zone holding the base
/// domain. It is stored encrypted on disk and never leaves this machine.
/// </para>
/// </summary>
public sealed class CloudflareDnsProvider(
    string apiToken,
    string zoneId,
    bool proxied,
    int ttl,
    HttpClient? httpClient = null) : IDnsProvider, IDisposable
{
    private const string ApiRoot = "https://api.cloudflare.com/client/v4";

    private readonly HttpClient _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    private readonly bool _ownsHttpClient = httpClient is null;
    private readonly string _apiToken = apiToken;
    private readonly string _zoneId = zoneId;

    public string DisplayName => "Cloudflare";

    public bool IsAutomated => true;

    public async Task<string?> CreateRecordAsync(
        string hostname,
        string target,
        bool allowProxy,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostname);
        ArgumentException.ThrowIfNullOrWhiteSpace(target);

        var type = IPAddress.TryParse(target, out var ip)
            ? ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? "AAAA" : "A"
            : "CNAME";

        // Idempotent by design: an existing record for this name is updated rather than
        // duplicated, so retrying a half-failed tunnel creation is always safe.
        var existing = await FindRecordAsync(hostname, cancellationToken).ConfigureAwait(false);

        var body = new CloudflareRecordRequest
        {
            Type = type,
            Name = hostname,
            Content = target,
            Ttl = ttl,
            // The operator's preference only applies where proxying can work at all. Forcing a
            // grey cloud for TCP tunnels prevents a record that resolves but never connects.
            Proxied = proxied && allowProxy,
        };

        using var request = new HttpRequestMessage(
            existing is null ? HttpMethod.Post : HttpMethod.Put,
            existing is null
                ? $"{ApiRoot}/zones/{_zoneId}/dns_records"
                : $"{ApiRoot}/zones/{_zoneId}/dns_records/{existing}")
        {
            Content = JsonContent.Create(body),
        };

        var response = await SendAsync<CloudflareRecordResult>(request, cancellationToken).ConfigureAwait(false);
        return response?.Id ?? existing;
    }

    /// <summary>
    /// Creates or replaces a DNS-01 TXT record. TXT records are never proxied.
    /// </summary>
    public async Task<string?> CreateTxtRecordAsync(
        string hostname,
        string value,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostname);
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        var existing = await FindRecordAsync(hostname, cancellationToken).ConfigureAwait(false);
        var body = new CloudflareRecordRequest
        {
            Type = "TXT",
            Name = hostname,
            Content = value,
            Ttl = 60,
            Proxied = false,
        };

        using var request = new HttpRequestMessage(
            existing is null ? HttpMethod.Post : HttpMethod.Put,
            existing is null
                ? $"{ApiRoot}/zones/{_zoneId}/dns_records"
                : $"{ApiRoot}/zones/{_zoneId}/dns_records/{existing}")
        {
            Content = JsonContent.Create(body),
        };

        var response = await SendAsync<CloudflareRecordResult>(request, cancellationToken).ConfigureAwait(false);
        return response?.Id ?? existing;
    }

    public async Task RemoveRecordAsync(string hostname, string? recordId, CancellationToken cancellationToken = default)
    {
        var id = recordId;
        if (string.IsNullOrWhiteSpace(id))
        {
            id = await FindRecordAsync(hostname, cancellationToken).ConfigureAwait(false);
        }

        if (string.IsNullOrWhiteSpace(id))
        {
            return; // nothing to remove — already gone, or never created
        }

        using var request = new HttpRequestMessage(HttpMethod.Delete, $"{ApiRoot}/zones/{_zoneId}/dns_records/{id}");
        try
        {
            await SendAsync<CloudflareRecordResult>(request, cancellationToken).ConfigureAwait(false);
        }
        catch (DnsProviderException ex) when (ex.NotFound)
        {
            // Deleting something that is already gone is a success, not a failure.
        }
    }

    public async Task<DnsProviderStatus> TestAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_zoneId))
        {
            return new DnsProviderStatus(false, "Cloudflare zone id is empty.");
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{ApiRoot}/zones/{_zoneId}");
            var zone = await SendAsync<CloudflareZoneResult>(request, cancellationToken).ConfigureAwait(false);

            return zone is null
                ? new DnsProviderStatus(false, "Cloudflare accepted the request but returned no zone.")
                : new DnsProviderStatus(true, $"Connected to zone '{zone.Name}'.", zone.Name, zone.Id);
        }
        catch (DnsProviderException ex)
        {
            return new DnsProviderStatus(false, ex.Message);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new DnsProviderStatus(false, $"Could not reach the Cloudflare API: {ex.Message}");
        }
    }

    /// <summary>
    /// Finds the Cloudflare zone that contains a hostname by trying progressively shorter DNS
    /// suffixes. This lets the UI fill Zone ID automatically for base domains such as
    /// tunnel.example.com whose actual Cloudflare zone is example.com.
    /// </summary>
    public async Task<DnsProviderStatus> DiscoverZoneAsync(
        string hostname,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostname);

        var labels = hostname.Trim().Trim('.').Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (labels.Length < 2)
        {
            return new DnsProviderStatus(false, "Enter a valid base domain before discovering the Cloudflare zone.");
        }

        try
        {
            for (var start = 0; start <= labels.Length - 2; start++)
            {
                var candidate = string.Join('.', labels.Skip(start));
                using var request = new HttpRequestMessage(
                    HttpMethod.Get,
                    $"{ApiRoot}/zones?name={Uri.EscapeDataString(candidate)}&status=active&per_page=50");

                var zones = await SendAsync<List<CloudflareZoneResult>>(request, cancellationToken)
                    .ConfigureAwait(false);
                var zone = zones?.FirstOrDefault(z =>
                    string.Equals(z.Name, candidate, StringComparison.OrdinalIgnoreCase));
                if (zone is not null)
                {
                    return new DnsProviderStatus(
                        true,
                        $"Found Cloudflare zone '{zone.Name}'.",
                        zone.Name,
                        zone.Id);
                }
            }

            return new DnsProviderStatus(
                false,
                "No accessible Cloudflare zone was found for this base domain. Enter the Zone ID manually or give the token Zone Read permission.");
        }
        catch (DnsProviderException ex)
        {
            return new DnsProviderStatus(
                false,
                $"Could not discover the Cloudflare zone: {ex.Message} Enter the Zone ID manually if this token only has DNS Edit permission.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new DnsProviderStatus(false, $"Could not reach the Cloudflare API: {ex.Message}");
        }
    }

    private async Task<string?> FindRecordAsync(string hostname, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"{ApiRoot}/zones/{_zoneId}/dns_records?name={Uri.EscapeDataString(hostname)}");

        var records = await SendAsync<List<CloudflareRecordResult>>(request, cancellationToken).ConfigureAwait(false);
        return records is { Count: > 0 } ? records[0].Id : null;
    }

    private async Task<T?> SendAsync<T>(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiToken);

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var envelope = await response.Content
            .ReadFromJsonAsync<CloudflareEnvelope<T>>(cancellationToken)
            .ConfigureAwait(false);

        if (response.IsSuccessStatusCode && envelope?.Success == true)
        {
            return envelope.Result;
        }

        var detail = envelope?.Errors is { Count: > 0 }
            ? string.Join("; ", envelope.Errors.Select(e => $"{e.Code}: {e.Message}"))
            : $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}";

        throw new DnsProviderException(
            $"Cloudflare rejected the request — {detail}",
            response.StatusCode == HttpStatusCode.NotFound || envelope?.Errors?.Any(e => e.Code == 81044) == true);
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _http.Dispose();
        }
    }

    // ---- Wire shapes ------------------------------------------------------

    private sealed class CloudflareEnvelope<T>
    {
        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("result")]
        public T? Result { get; set; }

        [JsonPropertyName("errors")]
        public List<CloudflareError>? Errors { get; set; }
    }

    private sealed class CloudflareError
    {
        [JsonPropertyName("code")]
        public int Code { get; set; }

        [JsonPropertyName("message")]
        public string Message { get; set; } = "";
    }

    private sealed class CloudflareRecordResult
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = "";

        [JsonPropertyName("name")]
        public string Name { get; set; } = "";
    }

    private sealed class CloudflareZoneResult
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = "";

        [JsonPropertyName("name")]
        public string Name { get; set; } = "";
    }

    private sealed class CloudflareRecordRequest
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = "A";

        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("content")]
        public string Content { get; set; } = "";

        [JsonPropertyName("ttl")]
        public int Ttl { get; set; } = 1;

        [JsonPropertyName("proxied")]
        public bool Proxied { get; set; }
    }
}

public sealed class DnsProviderException(string message, bool notFound = false) : Exception(message)
{
    public bool NotFound { get; } = notFound;
}
