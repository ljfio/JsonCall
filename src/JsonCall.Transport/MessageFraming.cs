namespace JsonCall.Transport;

/// <summary>How messages are delimited on a byte stream (TCP, stdio, Unix domain sockets).</summary>
public enum MessageFraming
{
    /// <summary>
    /// <c>Content-Length: n</c> header lines, a blank line, then n bytes, as used by LSP. Works for any wire format.
    /// </summary>
    ContentLength = 0,

    /// <summary>
    /// One message per line, terminated by <c>\n</c> (a preceding <c>\r</c> is accepted and dropped on receive). There are
    /// no headers. A JSON message never contains a raw newline, so line framing is unambiguous, but it is only valid for
    /// compact JSON: binary serializers (CBOR, MessagePack) must use <see cref="ContentLength"/>.
    /// </summary>
    Newline = 1
}
