namespace Shiny.Net.HttpServer;

/// <summary>
/// Requires a verified webhook signature on a generated endpoint class or one of its methods.
/// <code>
/// [Route("/hooks")]
/// public class HookEndpoints
/// {
///     [Post("/github")]
///     [RequireWebhookSignature("github")]
///     public IResult GitHub(WebhookContext webhook) => ...;
/// }
///
/// builder.AddWebhooks(o => o.AddVerifier("github", WebhookSignature.GitHub(secret)));
/// app.UseWebhookVerification();
/// </code>
/// <para>
/// Enforced by <c>UseWebhookVerification()</c>, the way <c>[Authorize]</c> is enforced by
/// <c>UseAuthorization()</c>. A <see cref="Webhooks.WebhookContext"/> parameter on the method is bound
/// from the verified delivery, and throws when there is none — so an endpoint that takes one cannot
/// run unverified even when the middleware was forgotten.
/// </para>
/// <para>
/// On a method it replaces the class's verifier: a delivery comes from one sender.
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class RequireWebhookSignatureAttribute(string verifier) : Attribute
{
    /// <summary>Name of a verifier registered with <c>AddWebhooks(o => o.AddVerifier(...))</c>.</summary>
    public string Verifier { get; } = verifier;
}
