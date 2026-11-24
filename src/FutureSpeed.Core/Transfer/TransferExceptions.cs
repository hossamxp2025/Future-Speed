using FutureSpeed.Core.Models;

namespace FutureSpeed.Core.Transfer;

/// <summary>The destination volume cannot hold the transfer; the job never starts.</summary>
public sealed class InsufficientSpaceException : Exception
{
    public InsufficientSpaceException(SpaceValidationResult validation)
        : base($"Not enough free space on '{validation.DestinationRoot}'. " +
               $"Required: {validation.RequiredBytes} bytes, available: {validation.AvailableBytes} bytes, " +
               $"missing: {validation.MissingBytes} bytes.")
        => Validation = validation;

    public SpaceValidationResult Validation { get; }
}

/// <summary>The destination is unusable (same as source, nested in the source, missing).</summary>
public sealed class InvalidDestinationException : Exception
{
    public InvalidDestinationException(IReadOnlyList<string> reasons)
        : base("Invalid destination: " + string.Join(" | ", reasons))
        => Reasons = reasons;

    public IReadOnlyList<string> Reasons { get; }
}

/// <summary>The volume filled up or disappeared mid-transfer; remaining files are abandoned.</summary>
public sealed class TransferAbortedException : Exception
{
    public TransferAbortedException(string message, Exception? inner = null) : base(message, inner) { }
}
