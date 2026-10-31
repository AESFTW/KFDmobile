namespace KfdMobile;

// This exception is emitted only before any key-write command has been sent.
public sealed class KeyloadPreparationException : IOException
{
    public bool CanRetry { get; }
    public KeyloadPreparationException(Exception cause)
        : base("Radio preparation failed before any keys were sent. " + cause.Message, cause)
    {
        CanRetry = cause is TimeoutException ||
            (cause is FormatException && cause.Message.StartsWith("Radio response frame", StringComparison.Ordinal)) ||
            cause.Message.StartsWith("Radio handshake failed:", StringComparison.Ordinal) ||
            cause.Message == "mr: unexpected opcode";
    }
}
