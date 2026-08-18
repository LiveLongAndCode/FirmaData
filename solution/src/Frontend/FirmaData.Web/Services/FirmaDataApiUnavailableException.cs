namespace FirmaData.Web.Services;

// Thrown for anything that isn't a well-formed API response the UI can render: a network
// failure, the typed client's own resilience pipeline timing out or its circuit being open, or
// an unexpected status code. Program.cs's exception handler turns this into the Danish error
// page (plan section 15) instead of a stack trace.
public sealed class FirmaDataApiUnavailableException : Exception
{
    public FirmaDataApiUnavailableException(string message)
        : base(message)
    {
    }

    public FirmaDataApiUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
