using FirmaData.Contracts;
using FirmaData.Web.Controllers;
using FirmaData.Web.Models;
using FirmaData.Web.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using NSubstitute;

namespace FirmaData.Web.Tests;

public class CompaniesControllerTests
{
    private static readonly CompanyDto LbForsikring = new(
        "16500836", "LB FORSIKRING A/S",
        new AddressDto("Amerika Plads 15", "2100", "København Ø"),
        "651200", "Anden forsikring", 1010);

    private static readonly SourcesDto Sources = new("apicvr.dk", "api.statbank.dk/ERHV1");

    private static CompaniesController CreateController(IFirmaDataApiClient apiClient)
    {
        var httpContext = new DefaultHttpContext();
        return new CompaniesController(apiClient)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
            TempData = new TempDataDictionary(httpContext, Substitute.For<ITempDataProvider>()),
        };
    }

    [Fact]
    public async Task Details_WhenFound_RendersTheDetailViewMappedFromTheResponse()
    {
        var statistics = new IndustryStatisticsDto("651200", 2022, 166, 15206, 13458, 10380m);
        var response = new EnrichedCompanyResponse(LbForsikring, statistics, "Ok", DateTimeOffset.UtcNow, Sources);
        var apiClient = Substitute.For<IFirmaDataApiClient>();
        apiClient.GetByCvrAsync("16500836", 2022, Arg.Any<CancellationToken>())
            .Returns(new CompanyLookupResult(CompanyLookupOutcome.Found, response));
        var sut = CreateController(apiClient);

        var result = await sut.Details("16500836", 2022, CancellationToken.None);

        var model = result.Should().BeOfType<ViewResult>().Subject.Model.Should().BeOfType<CompanyDetailViewModel>().Subject;
        model.Name.Should().Be("LB FORSIKRING A/S");
        model.WorkplacesDisplay.Should().Be("166");
    }

    [Fact]
    public async Task Details_WhenNotFound_RedirectsHomeWithADanishNotFoundMessage()
    {
        var apiClient = Substitute.For<IFirmaDataApiClient>();
        apiClient.GetByCvrAsync("99999999", 2022, Arg.Any<CancellationToken>())
            .Returns(new CompanyLookupResult(CompanyLookupOutcome.NotFound));
        var sut = CreateController(apiClient);

        var result = await sut.Details("99999999", 2022, CancellationToken.None);

        result.Should().BeOfType<RedirectToActionResult>().Which.ActionName.Should().Be("Index");
        sut.TempData["SearchError"].Should().Be("Der blev ikke fundet en virksomhed med CVR-nummer 99999999.");
    }

    [Fact]
    public async Task Details_WhenInvalid_RedirectsHomeWithADanishChecksumMessage()
    {
        var apiClient = Substitute.For<IFirmaDataApiClient>();
        apiClient.GetByCvrAsync("16500837", 2022, Arg.Any<CancellationToken>())
            .Returns(new CompanyLookupResult(CompanyLookupOutcome.Invalid));
        var sut = CreateController(apiClient);

        var result = await sut.Details("16500837", 2022, CancellationToken.None);

        result.Should().BeOfType<RedirectToActionResult>().Which.ActionName.Should().Be("Index");
        sut.TempData["SearchError"].Should().Be("Det indtastede CVR-nummer er ugyldigt (forkert kontrolciffer).");
    }

    [Fact]
    public async Task Details_WhenYearIsOmitted_ResolvesTheDefaultYearFromTheApiFirst()
    {
        var apiClient = Substitute.For<IFirmaDataApiClient>();
        apiClient.GetAvailableYearsAsync(Arg.Any<CancellationToken>())
            .Returns(new AvailableYearsResponse([2021, 2022], 2022));
        var response = new EnrichedCompanyResponse(LbForsikring, null, "Ok", DateTimeOffset.UtcNow, Sources);
        apiClient.GetByCvrAsync("16500836", 2022, Arg.Any<CancellationToken>())
            .Returns(new CompanyLookupResult(CompanyLookupOutcome.Found, response));
        var sut = CreateController(apiClient);

        await sut.Details("16500836", null, CancellationToken.None);

        await apiClient.Received(1).GetByCvrAsync("16500836", 2022, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Results_WithNoMatches_RendersAnEmptyResultsListRatherThanAnError()
    {
        var apiClient = Substitute.For<IFirmaDataApiClient>();
        apiClient.SearchByNameAsync("Ukendt Firma", 2022, Arg.Any<CancellationToken>())
            .Returns(new List<EnrichedCompanyResponse>());
        var sut = CreateController(apiClient);

        var result = await sut.Results("Ukendt Firma", 2022, CancellationToken.None);

        var model = result.Should().BeOfType<ViewResult>().Subject.Model.Should().BeOfType<SearchResultsViewModel>().Subject;
        model.Companies.Should().BeEmpty();
    }

    [Fact]
    public async Task Results_WithMatches_MapsEachResponseToASummaryRow()
    {
        var response = new EnrichedCompanyResponse(LbForsikring, null, "Ok", DateTimeOffset.UtcNow, Sources);
        var apiClient = Substitute.For<IFirmaDataApiClient>();
        apiClient.SearchByNameAsync("LB", 2022, Arg.Any<CancellationToken>())
            .Returns(new List<EnrichedCompanyResponse> { response });
        var sut = CreateController(apiClient);

        var result = await sut.Results("LB", 2022, CancellationToken.None);

        var model = result.Should().BeOfType<ViewResult>().Subject.Model.Should().BeOfType<SearchResultsViewModel>().Subject;
        model.Companies.Should().ContainSingle().Which.Name.Should().Be("LB FORSIKRING A/S");
    }

    [Fact]
    public async Task Results_WithABlankName_RedirectsHomeWithoutCallingTheApi()
    {
        var apiClient = Substitute.For<IFirmaDataApiClient>();
        var sut = CreateController(apiClient);

        var result = await sut.Results("   ", null, CancellationToken.None);

        result.Should().BeOfType<RedirectToActionResult>().Which.ActionName.Should().Be("Index");
        await apiClient.DidNotReceiveWithAnyArgs().SearchByNameAsync(default!, default, default);
    }
}
