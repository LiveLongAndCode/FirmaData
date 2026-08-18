using System.Diagnostics.Metrics;

namespace FirmaData.Cvr;

// firmadata.circuit.state (plan section 7.2), tagged dependency="cvr". An ObservableGauge reads
// current state on scrape rather than being pushed a value, since circuit state only changes on
// Polly's own OnOpened/OnClosed/OnHalfOpened callbacks (wired in ServiceCollectionExtensions),
// not on every request.
internal static class CircuitStateMetrics
{
    private const int Closed = 0;
    private const int Open = 1;
    private const int HalfOpen = 2;

    private static readonly Meter Meter = new("FirmaData");
    private static int _state = Closed;

    static CircuitStateMetrics() =>
        Meter.CreateObservableGauge(
            "firmadata.circuit.state",
            () => new Measurement<int>(Volatile.Read(ref _state), new KeyValuePair<string, object?>("dependency", "cvr")));

    public static void RecordClosed() => Volatile.Write(ref _state, Closed);

    public static void RecordOpened() => Volatile.Write(ref _state, Open);

    public static void RecordHalfOpened() => Volatile.Write(ref _state, HalfOpen);
}
