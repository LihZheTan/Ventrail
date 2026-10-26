using System;

namespace Ventrail.Services.Contracts;

/// <summary>
/// An error whose message is already written in plain language for the user
/// (e.g. "close the file in Word and try again"). The ViewModel shows it as-is,
/// without the generic "Something went wrong:" prefix.
/// </summary>
public class VentrailException : Exception
{
    public VentrailException(string message, Exception? inner = null) : base(message, inner) { }
}
