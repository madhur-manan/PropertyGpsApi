using System.ComponentModel.DataAnnotations;

namespace PropertyGpsApi.Infrastructure.Options;

/// <summary>
/// Which proxies this API is willing to believe.
///
/// Behind BBMP's reverse proxy every request arrives from the proxy's address, so the
/// per-IP rate limiter on the OTP routes sees one client and protects nothing. Reading
/// X-Forwarded-For fixes that - but only if the header can be trusted, and a header is
/// trustworthy exactly as far as the hop that set it.
///
/// So this is deliberately opt-in. With nothing configured the headers are ignored and
/// behaviour is unchanged, because the alternative default - believing whatever any client
/// writes in X-Forwarded-For - would let an attacker mint a fresh identity per request and
/// walk straight through the limiter. That is a worse position than the one being fixed.
/// </summary>
public sealed class NetworkOptions
{
    public const string Section = "Network";

    /// <summary>
    /// Individual proxy addresses to trust, e.g. "10.0.0.8". Loopback is always trusted by
    /// the framework, which is why this is empty on a developer machine.
    /// </summary>
    public string[] KnownProxies { get; init; } = [];

    /// <summary>
    /// Trusted proxy networks in CIDR form, e.g. "172.31.0.0/16". Use this when the proxy
    /// sits behind a load balancer and its address is not fixed.
    /// </summary>
    public string[] KnownNetworks { get; init; } = [];

    /// <summary>
    /// How many proxy hops to walk back through. One is right for a single reverse proxy
    /// and is the safe default: a larger number lets a client prepend entries of its own
    /// and have one of them believed.
    /// </summary>
    [Range(1, 8)]
    public int ForwardLimit { get; init; } = 1;
}
