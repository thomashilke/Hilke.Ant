namespace Hilke.Ant.Plus;

/// <summary>Base type for every exception observable from a public <c>Hilke.Ant.Plus</c> API.</summary>
public abstract class AntPlusException : Exception
{
    /// <summary>Create the exception with the given message.</summary>
    protected AntPlusException(string message) : base(message) { }
}

/// <summary>The radio is busy: scan/channel mutual-exclusion was violated.</summary>
public sealed class AntPlusBusyException : AntPlusException
{
    internal AntPlusBusyException(string message) : base(message) { }
}

/// <summary>A command did not receive a response within the configured timeout.</summary>
public sealed class AntPlusTimeoutException : AntPlusException
{
    internal AntPlusTimeoutException(string message) : base(message) { }
}

/// <summary>A command was rejected by the device, or attempted from an invalid channel state.</summary>
public sealed class AntPlusCommandException : AntPlusException
{
    internal AntPlusCommandException(string message) : base(message) { }
}
