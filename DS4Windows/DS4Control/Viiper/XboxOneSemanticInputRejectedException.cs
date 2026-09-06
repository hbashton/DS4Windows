using System.IO;

namespace DS4Windows;

/// <summary>
/// An exact, well-formed negative semantic-input acknowledgement. Input is
/// terminal for this stream, but its independent canonical-feedback direction
/// remains available for bounded one-shot device teardown. This is not a socket
/// failure or permission to replay/resynchronize the rejected incarnation.
/// </summary>
internal sealed class XboxOneSemanticInputRejectedException : IOException
{
    internal XboxOneSemanticInputRejectedException(ulong revision)
        : base("VIIPER rejected the Xbox One semantic-input revision; input is fenced while the exact device is retired.")
    {
        Revision = revision;
    }

    internal ulong Revision { get; }
}
