namespace FirmaData.Contracts;

// Makes provenance explicit: the caller can see the response was assembled from two systems.
public sealed record SourcesDto(string Company, string Statistics);
