using FutureSpeed.Core.Models;

namespace FutureSpeed.Core.Abstractions;

/// <summary>Pre-flight check that the destination can hold the planned transfer.</summary>
public interface ISpaceValidator
{
    SpaceValidationResult Validate(ScanResult scan, string destinationRoot, TransferOptions options);
}
