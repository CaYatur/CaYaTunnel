namespace CaYaTunnel.Core.Protocol;

/// <summary>
/// A stream that can signal "I am done sending" without tearing down the receive direction.
/// <para>
/// Forwarding needs this. An HTTP client sends its request and then waits; the response only
/// ends when the server closes its send side. If a wrapper in the chain swallows that signal,
/// the visitor waits forever for a response that has already fully arrived. Every stream the
/// pump can be handed therefore implements this so half-close survives wrapping.
/// </para>
/// </summary>
public interface IHalfClosable
{
    ValueTask CompleteWriteAsync(CancellationToken cancellationToken = default);
}
