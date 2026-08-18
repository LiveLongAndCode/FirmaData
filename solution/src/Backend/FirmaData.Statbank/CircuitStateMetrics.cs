using System.Diagnostics.Metrics;

namespace FirmaData.Statbank;

// firmadata.circuit.state (plan section 7.2), tagged dependency="statbank". Mirrors
// FirmaData.Cvr's CircuitStateMetrics -- duplicated rather than shared since the two adapter
// projects don't reference each other (plan section 2.1).
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
            () => new Measurement<int>(Volatile.Read(ref _state), new KeyValuePair<string, object?>("dependency", "statbank")));

    public static void RecordClosed() => Volatile.Write(ref _state, Closed);

    public static void RecordOpened() => Volatile.Write(ref _state, Open);

    public static void RecordHalfOpened() => Volatile.Write(ref _state, HalfOpen);
}
