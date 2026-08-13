using System;

namespace Desktop.Client.Services;

public class FatalErrorException : Exception
{
    public FatalErrorException(string message) : base(message) { }
    public FatalErrorException(string message, Exception innerException) : base(message, innerException) { }
}
