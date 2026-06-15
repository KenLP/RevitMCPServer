using System;

namespace RevitMCPAddin.Commands;

/// <summary>
/// A domain exception thrown by IRevitCommand implementations when the failure
/// is a predictable client/model-state error (element not found, read-only
/// parameter, etc.).  The dispatcher preserves <see cref="Code"/> in the error
/// envelope instead of collapsing it into the generic "command_failed" code,
/// which allows HTTP status mapping and client-side error handling to work
/// correctly.
/// </summary>
public sealed class RevitCommandException : Exception
{
    /// <summary>
    /// Short machine-readable code. Use one of the well-known values:
    ///   not_found, invalid_parameter, read_only_parameter,
    ///   unsupported_view, name_collision, ambiguous_selection.
    /// </summary>
    public string Code { get; }

    public RevitCommandException(string code, string message) : base(message)
        => Code = code;
}
