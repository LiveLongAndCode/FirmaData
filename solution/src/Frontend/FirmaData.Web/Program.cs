using FirmaData.Web.Services;

namespace FirmaData.Web;

public static class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        builder.Services.AddControllersWithViews();
        builder.Services.AddFirmaDataApiClient(builder.Configuration);

        var app = builder.Build();

        // The Danish error page is used in every environment, not just non-Development, so
        // "stop the API, see the friendly page, not a stack trace" (plan section 15) is
        // demoable with a plain `dotnet run` too, not only a production deployment.
        app.UseExceptionHandler("/Home/Error");

        if (!app.Environment.IsDevelopment())
        {
            // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
            app.UseHsts();
        }

        app.UseHttpsRedirection();
        app.UseStaticFiles();

        app.UseRouting();

        app.UseAuthorization();

        app.MapControllerRoute(
            name: "default",
            pattern: "{controller=Home}/{action=Index}/{id?}");

        app.Run();
    }
}
