using System.Reflection;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;
using ZapMQ.Core;
using ZapMQ.Server.Admin;
using ZapMQ.Server.V2;

namespace ZapMQ.Server.Panel;

/// <summary>
/// The panel on a port of its own: the interface, its API and the administration routes, all
/// behind the login. Nothing of it answers on the messaging port.
/// </summary>
public static class PanelHost
{
    public const string UserItem = "zapmq.panel.user";

    /// <summary>
    /// Marks the connections that came in through the panel port, whatever number it has.
    /// </summary>
    private sealed class PanelListener;

    private static readonly PanelListener Marker = new();

    public static bool IsPanel(HttpContext context) => context.Features.Get<PanelListener>() is not null;

    public static void ListenPanel(this KestrelServerOptions kestrel, PanelOptions options) =>
        kestrel.ListenAnyIP(options.Port, listen => listen.Use(next => connection =>
        {
            connection.Features.Set(Marker);
            return next(connection);
        }));

    public static void UsePanel(this WebApplication app, ServerOptions options, DateTimeOffset startedAt)
    {
        var basePath = "/" + options.Panel.BasePath.Trim('/');
        var files = OpenInterface(app.Logger);
        var types = new FileExtensionContentTypeProvider();

        app.MapWhen(IsPanel, panel =>
        {
            // Behind a reverse proxy the requests arrive under the base path; straight to the
            // port, without it. Both work.
            if (basePath != "/")
                panel.UsePathBase(basePath);

            panel.UseRouting();
            panel.Use(RequireSession);
            panel.UseEndpoints(endpoints =>
            {
                endpoints.MapPanelApi(options, startedAt);
                endpoints.MapAdmin();
                endpoints.MapGet("/health", () => Results.Json(new { status = "ok" }));
                endpoints.MapGet("/metrics", (Broker broker, V2Connections connections) => Results.Json(ServerHost.Metrics(broker, connections)));
            });
            panel.Run(context => ServeInterface(context, files, types));
        });
    }

    /// <summary>
    /// Everything but the interface itself and the way in asks for a session.
    /// </summary>
    private static Task RequireSession(HttpContext context, RequestDelegate next)
    {
        var path = context.Request.Path;
        var guarded = path.StartsWithSegments("/api") || path.StartsWithSegments("/admin") || path.StartsWithSegments("/metrics");
        var open = path.StartsWithSegments("/api/login") || path.StartsWithSegments("/api/logout") || path.StartsWithSegments("/api/session");
        if (!guarded || open)
            return next(context);

        var auth = context.RequestServices.GetRequiredService<PanelAuth>();
        if (auth.Session(context.Request.Cookies[PanelAuth.Cookie]) is not { } user)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return context.Response.WriteAsJsonAsync(new { error = "Sessão expirada ou inexistente" });
        }

        // Only the master sees and changes who else may get in.
        if (path.StartsWithSegments("/api/users") && !user.Master)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return context.Response.WriteAsJsonAsync(new { error = "Só o usuário master gerencia os usuários" });
        }

        context.Items[UserItem] = user.Login;
        return next(context);
    }

    private static IFileProvider? OpenInterface(ILogger logger)
    {
        try
        {
            var files = new ManifestEmbeddedFileProvider(Assembly.GetExecutingAssembly(), "wwwroot");
            if (files.GetFileInfo("index.html").Exists)
                return files;
        }
        catch (InvalidOperationException)
        {
            // Built without the interface.
        }
        logger.LogWarning("This build does not carry the panel interface; only its API answers on the panel port");
        return null;
    }

    private static async Task ServeInterface(HttpContext context, IFileProvider? files, FileExtensionContentTypeProvider types)
    {
        if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        if (files is null)
        {
            context.Response.ContentType = "text/plain; charset=utf-8";
            await context.Response.WriteAsync("ZapMQ " + ServerHost.Version + ": this build does not carry the panel interface.");
            return;
        }

        var path = context.Request.Path.Value ?? "/";
        var file = path.Length > 1 ? files.GetFileInfo(path.TrimStart('/')) : null;
        if (file is { Exists: true, IsDirectory: false })
        {
            context.Response.ContentType = types.TryGetContentType(file.Name, out var type) ? type : "application/octet-stream";
            // The names of the compiled files change with their contents.
            context.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
            await using var stream = file.CreateReadStream();
            await stream.CopyToAsync(context.Response.Body);
            return;
        }

        // A file of the interface that is not there is a missing file; anything else is a screen
        // of the interface, which is one page. The files are all at the root or under "media";
        // deeper than that, a dot is part of a name (a queue called "orders.new"), not an extension.
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (Path.HasExtension(path) && (segments.Length == 1 || (segments.Length == 2 && segments[0] == "media")))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        string page;
        await using (var stream = files.GetFileInfo("index.html").CreateReadStream())
        using (var reader = new StreamReader(stream))
            page = await reader.ReadToEndAsync();

        // The interface finds its files and its API relative to wherever it was published.
        var root = context.Request.PathBase.HasValue ? context.Request.PathBase.Value + "/" : "/";
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.Headers.CacheControl = "no-cache";
        await context.Response.WriteAsync(page.Replace("<base href=\"/\">", $"<base href=\"{root}\">"));
    }
}
