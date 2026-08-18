using System.ComponentModel.DataAnnotations;

namespace FirmaData.Api;

// Bounds on the name-search endpoint (plan fase 5, F3/F7): input length, how many results are
// returned, and how many concurrent Statbank calls enrichment is allowed to make. Kept as an
// ordinary Options class validated at startup, in the same style as CvrOptions/StatbankOptions --
// FirmaData.Application receives the two numeric limits it needs as plain constructor
// parameters (see Program.cs's factory registration), not this type or the Options package
// itself, since ArchitectureTests forbids the Application layer taking a dependency on either
// adapter project or picking up new package references for infrastructure concerns.
public sealed class SearchOptions
{
    public const string SectionName = "Search";

    [Range(1, int.MaxValue)]
    public int MinNameLength { get; set; } = 2;

    [Range(1, int.MaxValue)]
    public int MaxNameLength { get; set; } = 100;

    [Range(1, int.MaxValue)]
    public int DefaultLimit { get; set; } = 10;

    [Range(1, int.MaxValue)]
    public int MaxLimit { get; set; } = 25;

    [Range(1, int.MaxValue)]
    public int MaxConcurrentStatisticsCalls { get; set; } = 4;
}
