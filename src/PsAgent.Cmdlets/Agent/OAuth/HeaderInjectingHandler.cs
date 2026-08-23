using System.Net.Http;

namespace PsAgent.Cmdlets.Agent.OAuth;

/// <summary>
/// Adds fixed headers to every outgoing request.
/// </summary>
/// <remarks>
/// <para>Used for the headers a provider requires alongside a bearer token — Anthropic's OAuth
/// path needs <c>anthropic-beta: oauth-2025-04-20</c>, and without it the token is rejected as
/// though it were invalid, which sends you looking in the wrong place.</para>
/// <para>A handler rather than the SDK's <c>ExtraHeaders</c> property: that property is documented
/// but not publicly settable on <c>AnthropicClient</c>, so the supported route is to hand the
/// client an <see cref="HttpClient"/> that already does it.</para>
/// <para>Headers are added with <c>TryAddWithoutValidation</c> and only when absent, so this can
/// never displace the <c>Authorization</c> header the SDK sets.</para>
/// </remarks>
public sealed class HeaderInjectingHandler : DelegatingHandler
{
    private readonly IReadOnlyDictionary<string, string> _headers;

    /// <summary>Wrap the default handler, adding <paramref name="headers"/> to each request.</summary>
    public HeaderInjectingHandler(IReadOnlyDictionary<string, string> headers)
        : this(headers, new HttpClientHandler())
    {
    }

    /// <summary>Wrap a specific inner handler — the seam a test uses.</summary>
    public HeaderInjectingHandler(IReadOnlyDictionary<string, string> headers, HttpMessageHandler inner)
        : base(inner) => _headers = headers;

    /// <summary>Apply the headers to a request. Exposed so the rule is testable without a socket.</summary>
    public static void Apply(HttpRequestMessage request, IReadOnlyDictionary<string, string> headers)
    {
        foreach (var (name, value) in headers)
        {
            if (!request.Headers.Contains(name))
            {
                request.Headers.TryAddWithoutValidation(name, value);
            }
        }
    }

    /// <inheritdoc/>
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Apply(request, _headers);
        return base.SendAsync(request, cancellationToken);
    }
}
