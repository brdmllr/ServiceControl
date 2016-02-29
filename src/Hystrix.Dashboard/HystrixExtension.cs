namespace Hystrix.Dashboard
{
    using Microsoft.Owin;
    using Microsoft.Owin.FileSystems;
    using Microsoft.Owin.StaticFiles;
    using Owin;

    public static class HystrixExtension
    {
        public static IAppBuilder UseHystrixDashboard(this IAppBuilder app)
        {
            var physicalFileSystem = new PhysicalFileSystem(@"./www");
            var options = new FileServerOptions
            {
                EnableDefaultFiles = true,
                FileSystem = physicalFileSystem
            };
            options.StaticFileOptions.FileSystem = physicalFileSystem;
            options.StaticFileOptions.ServeUnknownFileTypes = true;
            options.DefaultFilesOptions.DefaultFileNames = new[]
            {
                "index.html"
            };
            options.RequestPath = new PathString("/hystrix");

            app.UseFileServer(options);

            return app;
        }
    }
}
