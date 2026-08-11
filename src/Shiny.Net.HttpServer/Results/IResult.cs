namespace Shiny.Net.HttpServer;

/// <summary>
/// A response a handler can return instead of writing to <see cref="HttpResponse"/> itself.
/// This is what generated endpoint methods return, and what makes them unit-testable — the
/// method hands back a value rather than performing I/O.
/// </summary>
public interface IResult
{
    ValueTask ExecuteAsync(HttpContext context);
}
