using System.Text;
using CaYaTunnel.Server.Routing;
using Xunit;

namespace CaYaTunnel.Tests;

/// <summary>
/// Rewriting the Host header is what makes localhost-only services answer through a tunnel: many
/// check it and refuse anything that is not localhost, which is how they defend against DNS
/// rebinding. Everything else has to survive byte for byte.
/// </summary>
public class HostRewritingTests
{
    private const string Target = "127.0.0.1:5600";

    [Fact]
    public async Task The_host_header_is_replaced_and_nothing_else_changes()
    {
        var request =
            "GET /api/0/info HTTP/1.1\r\n" +
            "Host: panel.tunnel.example.com:48771\r\n" +
            "Accept: */*\r\n" +
            "User-Agent: test\r\n\r\n";

        var result = await RewriteAsync(request);

        Assert.Contains($"Host: {Target}\r\n", result);
        Assert.DoesNotContain("panel.tunnel.example.com", result);
        Assert.StartsWith("GET /api/0/info HTTP/1.1\r\n", result);
        Assert.Contains("Accept: */*\r\n", result);
        Assert.Contains("User-Agent: test\r\n", result);
        Assert.EndsWith("\r\n\r\n", result);
    }

    [Fact]
    public async Task Every_request_on_a_keep_alive_connection_is_rewritten()
    {
        // Fixing only the first would produce a page that half works, which is worse than one
        // that plainly does not.
        var requests =
            "GET /one HTTP/1.1\r\nHost: public.example.com\r\n\r\n" +
            "GET /two HTTP/1.1\r\nHost: public.example.com\r\n\r\n" +
            "GET /three HTTP/1.1\r\nHost: public.example.com\r\n\r\n";

        var result = await RewriteAsync(requests);

        Assert.Equal(3, CountOccurrences(result, $"Host: {Target}"));
        Assert.DoesNotContain("public.example.com", result);
        Assert.Contains("GET /three HTTP/1.1", result);
    }

    [Fact]
    public async Task A_body_with_content_length_passes_through_untouched()
    {
        var body = """{"key":"value","host":"public.example.com"}""";
        var request =
            "POST /api/0/settings HTTP/1.1\r\n" +
            "Host: public.example.com\r\n" +
            "Content-Type: application/json\r\n" +
            $"Content-Length: {body.Length}\r\n\r\n" +
            body +
            "GET /after HTTP/1.1\r\nHost: public.example.com\r\n\r\n";

        var result = await RewriteAsync(request);

        // The body is data, not headers: the hostname inside it must survive verbatim.
        Assert.Contains(body, result);
        Assert.Contains("GET /after HTTP/1.1", result);
        Assert.Equal(2, CountOccurrences(result, $"Host: {Target}"));
    }

    [Fact]
    public async Task A_chunked_body_is_followed_so_the_next_request_is_still_found()
    {
        var request =
            "POST /upload HTTP/1.1\r\n" +
            "Host: public.example.com\r\n" +
            "Transfer-Encoding: chunked\r\n\r\n" +
            "5\r\nhello\r\n" +
            "6\r\n world\r\n" +
            "0\r\n\r\n" +
            "GET /next HTTP/1.1\r\nHost: public.example.com\r\n\r\n";

        var result = await RewriteAsync(request);

        Assert.Contains("5\r\nhello\r\n", result);
        Assert.Contains("6\r\n world\r\n", result);
        Assert.Contains("GET /next HTTP/1.1", result);
        Assert.Equal(2, CountOccurrences(result, $"Host: {Target}"));
    }

    [Fact]
    public async Task After_an_upgrade_the_bytes_are_left_completely_alone()
    {
        // A WebSocket frame is not HTTP and must never be parsed as if it were.
        var request =
            "GET /ws HTTP/1.1\r\n" +
            "Host: public.example.com\r\n" +
            "Upgrade: websocket\r\n" +
            "Connection: Upgrade\r\n\r\n" +
            "GET /not-a-request Host: something";

        var result = await RewriteAsync(request);

        Assert.Contains($"Host: {Target}\r\n", result);
        Assert.Contains("GET /not-a-request Host: something", result);
        Assert.Equal(1, CountOccurrences(result, $"Host: {Target}"));
    }

    [Fact]
    public async Task A_request_without_a_host_header_is_passed_through()
    {
        var request = "GET / HTTP/1.0\r\nAccept: */*\r\n\r\n";

        var result = await RewriteAsync(request);

        Assert.Equal(request, result);
    }

    [Fact]
    public async Task An_empty_connection_produces_nothing()
    {
        Assert.Equal(string.Empty, await RewriteAsync(string.Empty));
    }

    /// <summary>Pushes bytes through the rewriter and returns everything that came out.</summary>
    private static async Task<string> RewriteAsync(string input)
    {
        using var source = new MemoryStream(Encoding.ASCII.GetBytes(input));
        await using var rewriter = new HostRewritingStream(source, Target);

        var output = new MemoryStream();

        // Small reads on purpose: the rewriter must behave the same whether a request arrives in
        // one packet or a dozen.
        var buffer = new byte[7];
        while (true)
        {
            var read = await rewriter.ReadAsync(buffer);
            if (read == 0)
            {
                break;
            }

            output.Write(buffer, 0, read);
        }

        return Encoding.ASCII.GetString(output.ToArray());
    }

    internal static int Occurrences(string haystack, string needle) => CountOccurrences(haystack, needle);

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;

        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }
}
