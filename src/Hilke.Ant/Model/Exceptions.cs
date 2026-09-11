using Hilke.Ant.Protocol;

namespace Hilke.Ant.Model;

/// <summary>Base type for all library exceptions.</summary>
internal class AntException : Exception
{
    public AntException(string message) : base(message) { }
    public AntException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>A command was rejected by the device with a non-success response code.</summary>
internal sealed class AntCommandException : AntException
{
    public AntCommandException(AntMessageId command, ChannelResponseCode code)
        : base($"Command {command} failed with response code {code} (0x{(byte)code:X2}).")
    {
        Command = command;
        Code = code;
    }

    public AntMessageId Command { get; }
    public ChannelResponseCode Code { get; }
}

/// <summary>A command did not receive a response within the configured timeout.</summary>
internal sealed class AntTimeoutException : AntException
{
    public AntTimeoutException(AntMessageId command, TimeSpan timeout)
        : base($"Command {command} timed out after {timeout.TotalMilliseconds:F0} ms.")
    {
        Command = command;
        Timeout = timeout;
    }

    public AntMessageId Command { get; }
    public TimeSpan Timeout { get; }
}

/// <summary>An operation was attempted from an invalid channel state.</summary>
internal sealed class InvalidChannelStateException : AntException
{
    public InvalidChannelStateException(ChannelState current, string operation)
        : base($"Operation '{operation}' is invalid in channel state {current}.")
    {
        Current = current;
        Operation = operation;
    }

    public ChannelState Current { get; }
    public string Operation { get; }
}

/// <summary>The radio is busy: scan/channel mutual-exclusion was violated.</summary>
internal sealed class RadioBusyException : AntException
{
    public RadioBusyException(string message) : base(message) { }
}
