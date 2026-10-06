using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PropertyGpsApi.Infrastructure.Options;
using PropertyGpsApi.Infrastructure.Security.BodyEncryption;

namespace PropertyGpsApi.Tests;

/// <summary>
/// RSA key pairs generated for this test run only. Held as bytes, never as shared RSA objects:
/// test classes run in parallel and an RSA instance is not promised to be thread-safe.
/// </summary>
internal static class TestKeys
{
    private static readonly Lazy<byte[]> PrimaryPkcs8 = new(() => Generate(3072));
    private static readonly Lazy<byte[]> SecondaryPkcs8 = new(() => Generate(3072));
    private static readonly Lazy<byte[]> ShortPkcs8 = new(() => Generate(2048));

    /// <summary>The key the server under test holds.</summary>
    public static byte[] Primary => PrimaryPkcs8.Value;

    /// <summary>A valid key the server does NOT hold.</summary>
    public static byte[] Secondary => SecondaryPkcs8.Value;

    /// <summary>Under the 3072-bit floor.</summary>
    public static byte[] Short => ShortPkcs8.Value;

    public static string Base64(byte[] pkcs8) => Convert.ToBase64String(pkcs8);

    public static string Pem(byte[] pkcs8)
    {
        using var rsa = Import(pkcs8);
        return rsa.ExportPkcs8PrivateKeyPem();
    }

    public static string Pkcs1Pem(byte[] pkcs8)
    {
        using var rsa = Import(pkcs8);
        return rsa.ExportRSAPrivateKeyPem();
    }

    public static byte[] Spki(byte[] pkcs8)
    {
        using var rsa = Import(pkcs8);
        return rsa.ExportSubjectPublicKeyInfo();
    }

    /// <summary>Kid computed here independently of the production helper.</summary>
    public static string Kid(byte[] pkcs8) =>
        Convert.ToHexStringLower(SHA256.HashData(Spki(pkcs8))[..8]);

    public static RSA Import(byte[] pkcs8)
    {
        var rsa = RSA.Create();
        rsa.ImportPkcs8PrivateKey(pkcs8, out _);
        return rsa;
    }

    private static byte[] Generate(int bits)
    {
        using var rsa = RSA.Create(bits);
        return rsa.ExportPkcs8PrivateKey();
    }
}

/// <summary>One sealed body, and its parts, so tests can assert none of them reach a log.</summary>
internal sealed record SealedBody(string Envelope, string K, string N, string C, string Kid, long Ts)
{
    public byte[] Utf8 => Encoding.UTF8.GetBytes(Envelope);
}

/// <summary>
/// A C# sealer written from the wire spec, deliberately without the production helpers, so a
/// mistake in BodyEncryptionFormat cannot be mirrored here and pass unnoticed.
/// </summary>
internal static class TestSealer
{
    public const long DefaultTs = 1759480000;

    public static SealedBody Seal(
        string plaintext,
        string method,
        string path,
        string part = "body",
        string? bearerToken = null,
        long ts = DefaultTs,
        byte[]? publicKeyOf = null,
        string? kid = null,
        string enc = "pgps-body/1",
        byte[]? aesKey = null,
        byte[]? wrappedKey = null,
        bool flipTagBit = false) =>
        SealBytes(Encoding.UTF8.GetBytes(plaintext), method, path, part, bearerToken, ts, publicKeyOf, kid, enc,
            aesKey, wrappedKey, flipTagBit);

    public static SealedBody SealBytes(
        byte[] plaintext,
        string method,
        string path,
        string part = "body",
        string? bearerToken = null,
        long ts = DefaultTs,
        byte[]? publicKeyOf = null,
        string? kid = null,
        string enc = "pgps-body/1",
        byte[]? aesKey = null,
        byte[]? wrappedKey = null,
        bool flipTagBit = false)
    {
        var keyPair = publicKeyOf ?? TestKeys.Primary;
        kid ??= TestKeys.Kid(keyPair);

        var key = aesKey ?? RandomNumberGenerator.GetBytes(32);
        var nonce = RandomNumberGenerator.GetBytes(12);

        byte[] k;
        if (wrappedKey is not null)
        {
            k = wrappedKey;
        }
        else
        {
            using var rsa = RSA.Create();
            rsa.ImportSubjectPublicKeyInfo(TestKeys.Spki(keyPair), out _);
            k = rsa.Encrypt(key, RSAEncryptionPadding.OaepSHA256);
        }

        var tok = bearerToken is null
            ? "-"
            : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(bearerToken)))[..16];
        var aad = Encoding.UTF8.GetBytes(
            $"pgps-body/1|{kid}|{ts}|{method.ToUpperInvariant()}|{path.ToLowerInvariant()}|{part}|{tok}");

        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(key, 16))
            aes.Encrypt(nonce, plaintext, ciphertext, tag, aad);
        if (flipTagBit) tag[0] ^= 0x01;

        var c = ciphertext.Concat(tag).ToArray();
        var kB64 = Convert.ToBase64String(k);
        var nB64 = Convert.ToBase64String(nonce);
        var cB64 = Convert.ToBase64String(c);

        // Built by hand: System.Text.Json's default encoder would write '+' as +.
        var envelope = $"{{\"enc\":\"{enc}\",\"kid\":\"{kid}\",\"ts\":{ts},\"k\":\"{kB64}\",\"n\":\"{nB64}\",\"c\":\"{cB64}\"}}";
        return new SealedBody(envelope, kB64, nB64, cB64, kid, ts);
    }

    /// <summary>A key wrapped correctly, but not 32 bytes long.</summary>
    public static byte[] WrapWrongLengthKey(byte[]? publicKeyOf = null)
    {
        using var rsa = RSA.Create();
        rsa.ImportSubjectPublicKeyInfo(TestKeys.Spki(publicKeyOf ?? TestKeys.Primary), out _);
        return rsa.Encrypt(RandomNumberGenerator.GetBytes(16), RSAEncryptionPadding.OaepSHA256);
    }
}

/// <summary>Captures every line, including the exception text, so tests can prove what is NOT logged.</summary>
internal sealed class CapturingLogger<T> : ILogger<T>
{
    private readonly object _gate = new();
    private readonly List<LogLine> _lines = [];

    public IReadOnlyList<LogLine> Lines
    {
        get { lock (_gate) return _lines.ToList(); }
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        lock (_gate) _lines.Add(new LogLine(logLevel, formatter(state, exception), exception?.ToString()));
    }

    /// <summary>Everything a log sink could ever write: messages and exception text.</summary>
    public string AllText => string.Join("\n", Lines.Select(l => l.Message + "\n" + l.Exception));
}

internal sealed record LogLine(LogLevel Level, string Message, string? Exception);

/// <summary>Builds the filter and a ResourceExecutingContext around a DefaultHttpContext.</summary>
internal static class FilterHarness
{
    public const string WardSyncPath = "/v1/api/gbagps/singlesite/propertyinfo/ward-sync";
    public const string AddNewPath = "/v1/api/gbagps/singlesite/propertyinfo/add-new";
    public const string Token = "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjMifQ.c2lnbmF0dXJl";

    public static RequestEncryptionOptions Options(
        RequestEncryptionMode mode = RequestEncryptionMode.Optional,
        long maxEnvelopeBytes = RequestEncryptionOptions.DefaultMaxEnvelopeBytes,
        int maxAgeMinutes = 0,
        params string[] plainAllowedPaths) => new()
    {
        Mode = mode,
        Keys = new() { ["0"] = new RequestEncryptionKeyOptions { PrivateKey = TestKeys.Base64(TestKeys.Primary) } },
        MaxEnvelopeBytes = maxEnvelopeBytes,
        MaxAgeMinutes = maxAgeMinutes,
        PlainAllowedPaths = [.. plainAllowedPaths]
    };

    public static RequestBodyOpener Opener(RequestEncryptionOptions options, TimeProvider? clock = null) =>
        new(new RequestKeyRing(Microsoft.Extensions.Options.Options.Create(options)), clock ?? TimeProvider.System);

    public static RequestBodyDecryptionFilter Filter(
        RequestEncryptionOptions options,
        CapturingLogger<RequestBodyDecryptionFilter> log,
        TimeProvider? clock = null) =>
        new(Microsoft.Extensions.Options.Options.Create(options), Opener(options, clock), log);

    public static ParameterDescriptor BodyParameter() => new()
    {
        Name = "request",
        ParameterType = typeof(object),
        BindingInfo = new BindingInfo { BindingSource = BindingSource.Body }
    };

    /// <summary>[FromForm] string payload, as add-new binds it; or [FromForm(Name = boundName)] string name.</summary>
    public static ParameterDescriptor FormParameter(string name = "payload", string? boundName = null) => new()
    {
        Name = name,
        ParameterType = typeof(string),
        BindingInfo = new BindingInfo { BindingSource = BindingSource.Form, BinderModelName = boundName }
    };

    /// <summary>An action that binds uploaded files only (IFormFileCollection).</summary>
    public static ParameterDescriptor FormFileParameter() => new()
    {
        Name = "files",
        ParameterType = typeof(IFormFileCollection),
        BindingInfo = new BindingInfo { BindingSource = BindingSource.FormFile }
    };

    public static ParameterDescriptor QueryParameter() => new()
    {
        Name = "start",
        ParameterType = typeof(int),
        BindingInfo = new BindingInfo { BindingSource = BindingSource.Query }
    };

    public static ParameterDescriptor UnboundParameter() => new()
    {
        Name = "ct",
        ParameterType = typeof(CancellationToken),
        BindingInfo = new BindingInfo { BindingSource = BindingSource.Special }
    };

    public static ResourceExecutingContext Context(HttpContext http, params ParameterDescriptor[] parameters)
    {
        var action = new ActionDescriptor { Parameters = parameters.ToList() };
        var actionContext = new ActionContext(http, new RouteData(), action);
        return new ResourceExecutingContext(actionContext, new List<IFilterMetadata>(), new List<IValueProviderFactory>());
    }

    /// <summary>Runs the filter; true when it let the request through to model binding.</summary>
    public static async Task<bool> RunAsync(RequestBodyDecryptionFilter filter, ResourceExecutingContext context)
    {
        var reachedBinding = false;
        await filter.OnResourceExecutionAsync(context, () =>
        {
            reachedBinding = true;
            return Task.FromResult(new ResourceExecutedContext(context, context.Filters));
        });
        return reachedBinding;
    }

    /// <summary>A JSON request as the app sends it.</summary>
    public static DefaultHttpContext JsonRequest(
        string body,
        string path = WardSyncPath,
        string method = "POST",
        string? token = Token,
        bool header = true,
        string? contentType = "application/json",
        bool declareLength = true,
        Func<byte[], Stream>? stream = null)
    {
        var http = new DefaultHttpContext();
        var bytes = Encoding.UTF8.GetBytes(body);
        http.Request.Method = method;
        http.Request.Path = path;
        if (contentType is not null) http.Request.ContentType = contentType;
        http.Request.Body = stream?.Invoke(bytes) ?? new MemoryStream(bytes);
        if (declareLength) http.Request.ContentLength = bytes.Length;
        if (token is not null) http.Request.Headers.Authorization = "Bearer " + token;
        if (header) http.Request.Headers["X-Body-Enc"] = "1";
        return http;
    }

    public static async Task<string> ReadBodyAsync(HttpRequest request)
    {
        using var reader = new StreamReader(request.Body, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        return await reader.ReadToEndAsync();
    }
}

/// <summary>Hands out at most a few bytes per read, the way a slow mobile upload arrives.</summary>
internal sealed class TrickleStream(byte[] content, int maxPerRead = 3) : Stream
{
    private int _position;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        var n = Math.Min(Math.Min(buffer.Length, maxPerRead), content.Length - _position);
        content.AsSpan(_position, n).CopyTo(buffer);
        _position += n;
        return n;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        Task.FromResult(Read(buffer.AsSpan(offset, count)));

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(Read(buffer.Span));

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>Fails every read with the given exception, after an optional good prefix.</summary>
internal sealed class FailingStream(Exception failure, byte[]? prefix = null) : Stream
{
    private int _position;
    private readonly byte[] _prefix = prefix ?? [];

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (_position >= _prefix.Length) throw failure;
        var n = Math.Min(buffer.Length, _prefix.Length - _position);
        _prefix.AsSpan(_position, n).CopyTo(buffer);
        _position += n;
        return n;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        Task.FromResult(Read(buffer.AsSpan(offset, count)));

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(Read(buffer.Span));

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>Builds multipart/form-data bodies the way the app's MultipartRequest does.</summary>
internal static class Multipart
{
    public const string Boundary = "----pgps-test-boundary-7d1f";
    public const string ContentType = "multipart/form-data; boundary=" + Boundary;

    public static byte[] Build(IEnumerable<(string Name, string Value)> fields, IEnumerable<(string Name, string FileName, byte[] Content)>? files = null, bool close = true)
    {
        using var ms = new MemoryStream();
        void W(string s) => ms.Write(Encoding.UTF8.GetBytes(s));

        foreach (var (name, value) in fields)
        {
            W($"--{Boundary}\r\n");
            W($"Content-Disposition: form-data; name=\"{name}\"\r\n\r\n");
            W(value);
            W("\r\n");
        }

        foreach (var (name, fileName, content) in files ?? [])
        {
            W($"--{Boundary}\r\n");
            W($"Content-Disposition: form-data; name=\"{name}\"; filename=\"{fileName}\"\r\n");
            W("Content-Type: image/jpeg\r\n\r\n");
            ms.Write(content);
            W("\r\n");
        }

        if (close) W($"--{Boundary}--\r\n");
        return ms.ToArray();
    }

    public static DefaultHttpContext Request(
        byte[] body,
        string path = FilterHarness.AddNewPath,
        string? token = FilterHarness.Token,
        bool header = true,
        Stream? stream = null,
        string contentType = ContentType)
    {
        var http = new DefaultHttpContext();
        http.Request.Method = "POST";
        http.Request.Path = path;
        http.Request.ContentType = contentType;
        http.Request.Body = stream ?? new MemoryStream(body);
        http.Request.ContentLength = body.Length;
        if (token is not null) http.Request.Headers.Authorization = "Bearer " + token;
        if (header) http.Request.Headers["X-Body-Enc"] = "1";
        return http;
    }
}
