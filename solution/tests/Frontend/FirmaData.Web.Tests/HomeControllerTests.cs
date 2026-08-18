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

public class HomeControllerTests
{
    private static IFirmaDataApiClient CreateApiClient(int defaultYear, params int[] years)
    {
        var client = Substitute.For<IFirmaDataApiClient>();
        client.GetAvailableYearsAsync(Arg.Any<CancellationToken>())
            .Returns(new AvailableYearsResponse(years, defaultYear));
        return client;
    }

    private static HomeController CreateController(IFirmaDataApiClient apiClient)
    {
        var httpContext = new DefaultHttpContext { TraceIdentifier = "trace-123" };
        return new HomeController(apiClient)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
            TempData = new TempDataDictionary(httpContext, Substitute.For<ITempDataProvider>()),
        };
    }

    [Fact]
    public async Task Index_Get_PopulatesTheYearDropdownFromTheApi()
    {
        var sut = CreateController(CreateApiClient(2023, 2021, 2022, 2023));

        var result = await sut.Index(CancellationToken.None);

        var model = result.Should().BeOfType<ViewResult>().Subject.Model.Should().BeOfType<SearchViewModel>().Subject;
        model.Year.Should().Be(2023);
        model.AvailableYears.Should().Equal(2021, 2022, 2023);
    }

    [Fact]
    public async Task Index_Post_WithAnEightDigitQuery_RedirectsToCompaniesDetails()
    {
        var sut = CreateController(CreateApiClient(2022, 2022));
        var model = new SearchViewModel { Query = "16500836", Year = 2022 };

        var result = await sut.Index(model, CancellationToken.None);

        var redirect = result.Should().BeOfType<RedirectToActionResult>().Subject;
        redirect.ActionName.Should().Be("Details");
        redirect.ControllerName.Should().Be("Companies");
        redirect.RouteValues!["cvrNumber"].Should().Be("16500836");
        redirect.RouteValues!["year"].Should().Be(2022);
    }

    [Fact]
    public async Task Index_Post_WithANameQuery_RedirectsToCompaniesResults()
    {
        var sut = CreateController(CreateApiClient(2022, 2022));
        var model = new SearchViewModel { Query = "LB Forsikring", Year = 2022 };

        var result = await sut.Index(model, CancellationToken.None);

        var redirect = result.Should().BeOfType<RedirectToActionResult>().Subject;
        redirect.ActionName.Should().Be("Results");
        redirect.ControllerName.Should().Be("Companies");
        redirect.RouteValues!["name"].Should().Be("LB Forsikring");
    }

    [Fact]
    public async Task Index_Post_TreatsAnEightDigitQueryWithSurroundingWhitespaceAsACvrNumber()
    {
        var sut = CreateController(CreateApiClient(2022, 2022));
        var model = new SearchViewModel { Query = "  16500836  ", Year = 2022 };

        var result = await sut.Index(model, CancellationToken.None);

        result.Should().BeOfType<RedirectToActionResult>().Which.ActionName.Should().Be("Details");
    }

    [Fact]
    public async Task Index_Post_WithInvalidModelState_RedisplaysTheFormWithYearsReloaded()
    {
        var sut = CreateController(CreateApiClient(2022, 2021, 2022));
        sut.ModelState.AddModelError(nameof(SearchViewModel.Query), "Angiv et CVR-nummer eller et firmanavn.");
        var model = new SearchViewModel { Query = null };

        var result = await sut.Index(model, CancellationToken.None);

        var viewModel = result.Should().BeOfType<ViewResult>().Subject.Model.Should().BeOfType<SearchViewModel>().Subject;
        viewModel.AvailableYears.Should().Equal(2021, 2022);
    }

    [Fact]
    public void Error_ShowsTheCurrentRequestsTraceIdentifier()
    {
        var sut = CreateController(CreateApiClient(2022, 2022));

        var result = sut.Error();

        var model = result.Should().BeOfType<ViewResult>().Subject.Model.Should().BeOfType<ErrorViewModel>().Subject;
        model.RequestId.Should().Be("trace-123");
        model.ShowRequestId.Should().BeTrue();
    }
}
