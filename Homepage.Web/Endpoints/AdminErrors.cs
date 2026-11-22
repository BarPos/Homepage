using System.Net;
using Homepage.Blocks;
using Homepage.Content;

namespace Homepage.Web.Endpoints;

/// <summary>
/// Turns domain exceptions into responses: JSON for the editor, a flash fragment for htmx, a page otherwise.
/// Not found and access denied are kept distinct only where the user may already see the item.
/// </summary>
public sealed class AdminErrorFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        try
        {
            return await next(context);
        }
        catch (Exception ex) when (ex is NotFoundException or AccessDeniedException or BlockException or ConflictException)
        {
            var status = ex switch
            {
                NotFoundException => 404,
                AccessDeniedException => 403,
                ConflictException => 409,
                _ => 400,
            };
            var http = context.HttpContext;
            var message = ex.Message;
            if (http.Request.Headers.Accept.Any(a => a?.Contains("application/json", StringComparison.Ordinal) == true))
                return Results.Json(new { error = message }, statusCode: status);
            if (http.Request.Headers.ContainsKey("HX-Request"))
            {
                http.Response.Headers["HX-Retarget"] = "#flash";
                http.Response.Headers["HX-Reswap"] = "innerHTML";
                return Results.Content($"<div class=\"alert alert-error\">{WebUtility.HtmlEncode(message)}</div>", "text/html", statusCode: status);
            }
            return Rendering.Html.Render<Admin.AdminMessage>(new { Heading = status == 404 ? "Not found" : "Not possible", Text = message }, status);
        }
    }
}

public static class Flash
{
    /// <summary>htmx: show a message in the admin flash area (HX-Trigger hp-flash).</summary>
    public static void Ok(HttpResponse res, string message) =>
        res.Headers["HX-Trigger"] = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["hp-flash"] = new { message, kind = "ok" },
        });
}
