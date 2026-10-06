using System.Buffers;
using System.IO.Pipelines;
using System.Text;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;
using PropertyGpsApi.Common;
using PropertyGpsApi.Infrastructure.Options;

namespace PropertyGpsApi.Infrastructure.Security.BodyEncryption;

/// <summary>
/// Opens pgps-body/1 request bodies before model binding, so every controller, service, stored
/// procedure and table downstream sees exactly the plain values it saw before encryption existed.
///
/// A global resource filter: it runs after the exception handler, the rate limiter,
/// authentication, authorization and the per-action size and form-limit filters (those are
/// authorization filters, so add-new's 80 MB and 8 MB limits already apply to the form read
/// here), and before any model binder touches the body.
///
/// It only looks at POST, PUT and PATCH actions that bind a Body or Form parameter, and only in
/// the way that action reads its input: a form is parsed only for an action that binds a form
/// (so a multipart upload sent to a JSON action is never read, buffered or spilled to disk, and
/// the framework's 415 stands), and a JSON body is opened only for an action that binds a body.
/// A sealed form field is opened only when the action binds a form parameter of that exact name,
/// so one request can never make the server do more RSA work than the action has fields.
///
/// Whether a body is sealed is decided from the X-Body-Enc header AND the {"enc":"pgps-body/
/// marker:
///
///   header  marker  action
///   yes     yes     decrypt
///   yes     no      refuse (BODY_DECRYPT_FAILED)
///   no      yes     decrypt, and log that the header was stripped on the way
///   no      no      plain: Required refuses, Optional passes and logs, Off passes
///
/// Mode Off refuses any sealed body. Nothing logged here ever contains the plain text, k, n,
/// c, or the text of a cryptographic exception.
/// </summary>
internal sealed class RequestBodyDecryptionFilter(
    IOptions<RequestEncryptionOptions> options,
    RequestBodyOpener opener,
    ILogger<RequestBodyDecryptionFilter> logger) : IAsyncResourceFilter
{
    /// <summary>Enough to see the marker past a little leading whitespace.</summary>
    internal const int PeekBytes = 64;

    /// <summary>
    /// The most a sealed body's buffer reserves before its bytes have arrived. A declared
    /// Content-Length is only a promise: reserving all of it up front let a client that sends
    /// a few bytes and then stalls hold MaxEnvelopeBytes of memory per connection.
    /// </summary>
    internal const int MaxInitialBufferBytes = 64 * 1024;

    public async Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next)
    {
        var request = context.HttpContext.Request;
        if (IsBodyMethod(request.Method))
        {
            var binding = BindingOf(context.ActionDescriptor);
            if (binding.Any)
            {
                var settings = options.Value;
                var sealedHeader = !StringValues.IsNullOrEmpty(request.Headers[BodyEncryptionFormat.HeaderName]);

                // Read the request only the way the action itself would. A form sent to a JSON
                // action, or JSON sent to a form action, is never opened: it is peeked at like
                // any other body that cannot be sealed, and left for the framework to refuse.
                if (binding.Form && request.HasFormContentType)
                    await HandleFormAsync(context.HttpContext, settings, sealedHeader, binding.FormFields);
                else
                    await HandleBodyAsync(context.HttpContext, settings, sealedHeader,
                        canBeSealed: binding.Body && IsJsonContentType(request.ContentType));
            }
        }

        await next();
    }

    /// <summary>Only actions that will actually read the body: a Body- or Form-bound parameter.</summary>
    internal static bool AppliesTo(string method, ActionDescriptor action) =>
        IsBodyMethod(method) && BindingOf(action).Any;

    private static bool IsBodyMethod(string method) =>
        HttpMethods.IsPost(method) || HttpMethods.IsPut(method) || HttpMethods.IsPatch(method);

    /// <summary>
    /// How an action reads its input. <see cref="BodyBinding.FormFields"/> holds the names of
    /// its Form-bound parameters (the [FromForm(Name = ...)] name when one is given) - the only
    /// form fields that may arrive sealed. The app seals exactly one, add-new's "payload".
    /// </summary>
    internal static BodyBinding BindingOf(ActionDescriptor action)
    {
        var body = false;
        var form = false;
        HashSet<string>? fields = null;

        foreach (var parameter in action.Parameters)
        {
            var source = parameter.BindingInfo?.BindingSource;
            if (source is null) continue;

            if (source == BindingSource.Body)
            {
                body = true;
            }
            else if (source == BindingSource.FormFile)
            {
                form = true;
            }
            else if (source == BindingSource.Form)
            {
                form = true;
                // Case-insensitive, as the form collection and model binding are.
                (fields ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase))
                    .Add(parameter.BindingInfo!.BinderModelName ?? parameter.Name);
            }
        }

        return new BodyBinding(body, form, fields ?? NoFields);
    }

    private static readonly IReadOnlySet<string> NoFields = new HashSet<string>();

    internal readonly record struct BodyBinding(bool Body, bool Form, IReadOnlySet<string> FormFields)
    {
        public bool Any => Body || Form;
    }

    /// <summary>Every media type the JSON input formatter accepts.</summary>
    internal static bool IsJsonContentType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType) || !MediaTypeHeaderValue.TryParse(contentType, out var parsed))
            return false;

        var type = parsed.Type.Value;
        var subType = parsed.SubType.Value;
        if (type is null || subType is null) return false;

        if (type.Equals("application", StringComparison.OrdinalIgnoreCase))
            return subType.Equals("json", StringComparison.OrdinalIgnoreCase)
                   || subType.EndsWith("+json", StringComparison.OrdinalIgnoreCase);

        return type.Equals("text", StringComparison.OrdinalIgnoreCase)
               && subType.Equals("json", StringComparison.OrdinalIgnoreCase);
    }

    // ---- JSON (and any other non-form) body ----------------------------------------------

    private async Task HandleBodyAsync(HttpContext http, RequestEncryptionOptions settings, bool sealedHeader, bool canBeSealed)
    {
        var request = http.Request;

        if (request.ContentLength == 0)
        {
            Plain(http, settings, sealedHeader, empty: true);
            return;
        }

        var reader = request.BodyReader;
        var (empty, marker) = await GuardRead(http, () => PeekAsync(reader, http.RequestAborted));

        if (!marker || !canBeSealed)
        {
            // On a server whose request body is a stream rather than a native pipe (IIS
            // in-process is one), BodyReader wraps Body and now holds the peeked bytes, so a
            // formatter reading Body directly would start after them. Reading Body through the
            // pipe keeps every byte in order on both kinds of server.
            request.Body = reader.AsStream(leaveOpen: true);
            Plain(http, settings, sealedHeader, empty);
            return;
        }

        if (settings.Mode == RequestEncryptionMode.Off)
            throw Refuse(http, "sealed body while RequestEncryption:Mode is Off", BodyEncryptionErrors.KeyUnknown(),
                bytes: request.ContentLength);

        if (request.ContentLength > settings.MaxEnvelopeBytes)
            throw Refuse(http, "sealed body larger than RequestEncryption:MaxEnvelopeBytes", BodyEncryptionErrors.TooLarge(),
                bytes: request.ContentLength);

        var sealedBody = await GuardRead(http, () => ReadAllAsync(reader, settings.MaxEnvelopeBytes,
            request.ContentLength, http.RequestAborted));
        if (sealedBody is null)
            throw Refuse(http, "sealed body larger than RequestEncryption:MaxEnvelopeBytes", BodyEncryptionErrors.TooLarge(),
                bytes: request.ContentLength);

        var plaintext = Open(http, settings, sealedHeader, sealedBody, BodyEncryptionFormat.BodyPart);

        request.Body = new MemoryStream(plaintext, writable: false);
        request.ContentLength = plaintext.Length;
    }

    /// <summary>
    /// Looks at the first bytes without consuming them, so a plain body is never buffered (and
    /// never spilled to disk the way EnableBuffering would). Loops until PeekBytes have
    /// arrived or the body ends.
    /// </summary>
    private static async Task<(bool Empty, bool Marker)> PeekAsync(PipeReader reader, CancellationToken ct)
    {
        while (true)
        {
            var result = await reader.ReadAsync(ct);
            var buffer = result.Buffer;

            if (buffer.Length >= PeekBytes || result.IsCompleted)
            {
                var length = (int)Math.Min(buffer.Length, PeekBytes);
                var head = new byte[length];
                buffer.Slice(0, length).CopyTo(head);
                reader.AdvanceTo(buffer.Start, buffer.End);
                return (buffer.Length == 0, BodyEncryptionFormat.StartsWithMarker(head));
            }

            // Nothing consumed; everything so far examined, so the next read waits for more.
            reader.AdvanceTo(buffer.Start, buffer.End);
        }
    }

    /// <summary>
    /// The whole sealed body, consumed as it arrives (so the server's own request buffer never
    /// fills and stalls the upload), or null once it exceeds <paramref name="max"/>.
    /// </summary>
    private static async Task<byte[]?> ReadAllAsync(PipeReader reader, long max, long? contentLength, CancellationToken ct)
    {
        using var collected = new MemoryStream(InitialBufferBytes(contentLength, max));
        while (true)
        {
            var result = await reader.ReadAsync(ct);
            var buffer = result.Buffer;

            if (collected.Length + buffer.Length > max)
            {
                reader.AdvanceTo(buffer.End);
                return null;
            }

            foreach (var segment in buffer) collected.Write(segment.Span);
            reader.AdvanceTo(buffer.End);

            if (result.IsCompleted) return collected.ToArray();
        }
    }

    /// <summary>
    /// What to reserve for a sealed body before it arrives: its declared length, but never more
    /// than <see cref="MaxInitialBufferBytes"/>. Beyond that the buffer grows with the bytes
    /// actually received, so memory follows what a client sends, not what it claims it will.
    /// </summary>
    internal static int InitialBufferBytes(long? contentLength, long max) =>
        contentLength is { } declared && declared > 0
            ? (int)Math.Min(Math.Min(declared, max), MaxInitialBufferBytes)
            : 0;

    // ---- Form (the survey submit) --------------------------------------------------------

    private async Task HandleFormAsync(HttpContext http, RequestEncryptionOptions settings, bool sealedHeader,
        IReadOnlySet<string> boundFields)
    {
        var request = http.Request;
        var form = await GuardRead(http, () => request.ReadFormAsync(http.RequestAborted));

        // Every sealed field is vetted here, before the first one is opened: each open costs an
        // RSA private-key operation, so a form must not be able to ask for more of them than the
        // action has form fields (the form collection's names are unique, case-insensitively).
        var sealedFields = new List<string>();
        var plainFields = 0;
        foreach (var (name, values) in form)
        {
            var sealedValues = values.Count(BodyEncryptionFormat.StartsWithMarker);
            if (sealedValues > 0)
            {
                if (values.Count != 1)
                    throw Refuse(http, "a sealed form field must carry exactly one value", BodyEncryptionErrors.DecryptFailed());
                if (!boundFields.Contains(name))
                    throw Refuse(http, "a sealed form field that the action does not bind", BodyEncryptionErrors.DecryptFailed());
                sealedFields.Add(name);
            }
            else if (values.Any(v => !string.IsNullOrEmpty(v)))
            {
                plainFields++;
            }
        }

        if (sealedFields.Count == 0)
        {
            Plain(http, settings, sealedHeader, empty: form.Count == 0 && form.Files.Count == 0);
            return;
        }

        if (settings.Mode == RequestEncryptionMode.Off)
            throw Refuse(http, "sealed form field while RequestEncryption:Mode is Off", BodyEncryptionErrors.KeyUnknown());

        if (plainFields > 0)
        {
            if (settings.Mode == RequestEncryptionMode.Required && !IsPlainAllowed(settings, request.Path))
                throw Refuse(http, "plain form field beside a sealed one while RequestEncryption:Mode is Required",
                    BodyEncryptionErrors.Required());
            if (settings.Mode == RequestEncryptionMode.Optional)
                logger.LogInformation("Plain form field(s) accepted beside sealed ones on {Method} {Path} (RequestEncryption:Mode is Optional)",
                    request.Method, request.Path);
        }

        var fields = new Dictionary<string, StringValues>(form.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var (name, values) in form) fields[name] = values;

        foreach (var name in sealedFields)
        {
            // Measured before it is copied: a UTF-8 encoding is never shorter than the text.
            var text = form[name][0]!;
            if (text.Length > settings.MaxEnvelopeBytes)
                throw Refuse(http, "sealed form field larger than RequestEncryption:MaxEnvelopeBytes", BodyEncryptionErrors.TooLarge(),
                    bytes: text.Length);

            var sealedValue = Encoding.UTF8.GetBytes(text);
            if (sealedValue.Length > settings.MaxEnvelopeBytes)
                throw Refuse(http, "sealed form field larger than RequestEncryption:MaxEnvelopeBytes", BodyEncryptionErrors.TooLarge(),
                    bytes: sealedValue.Length);

            var plaintext = Open(http, settings, sealedHeader, sealedValue, BodyEncryptionFormat.FormPartPrefix + name);
            fields[name] = Encoding.UTF8.GetString(plaintext);
        }

        // [FromForm] binding and Request.Form.Files both read this collection: the plain text,
        // and the very same IFormFile objects (nothing about a file is copied).
        request.Form = new FormCollection(fields, form.Files);
    }

    // ---- Shared ----------------------------------------------------------------------------

    private byte[] Open(HttpContext http, RequestEncryptionOptions settings, bool sealedHeader, byte[] sealedBytes, string part)
    {
        var request = http.Request;
        var aad = new BodyAadContext(
            request.Method,
            string.IsNullOrEmpty(request.Path.Value) ? "/" : request.Path.Value,
            part,
            BodyEncryptionFormat.TokenTag(BodyEncryptionFormat.BearerToken(request.Headers.Authorization.ToString())));

        var result = opener.TryOpen(sealedBytes, aad, settings.MaxAgeMinutes);
        if (!result.Succeeded)
            throw Refuse(http, result.Failure.ToString(), BodyEncryptionErrors.ForOpenFailure(result.Failure),
                result.Kid, sealedBytes.Length, part);

        if (!sealedHeader)
            logger.LogInformation(
                "Sealed {Part} arrived without the {Header} header on {Method} {Path} (kid {Kid}); opened anyway - something on the way stripped it",
                part, BodyEncryptionFormat.HeaderName, request.Method, request.Path, result.Kid);

        logger.LogDebug("Opened sealed {Part} on {Method} {Path}: kid {Kid}, ts {Ts}, {Bytes} bytes sealed",
            part, request.Method, request.Path, result.Kid, result.Ts, sealedBytes.Length);

        return result.Plaintext!;
    }

    /// <summary>The bottom row of the decision table, plus the header-without-marker row.</summary>
    private void Plain(HttpContext http, RequestEncryptionOptions settings, bool sealedHeader, bool empty)
    {
        var request = http.Request;

        if (sealedHeader)
            throw Refuse(http, $"{BodyEncryptionFormat.HeaderName} is set but the body is not a sealed envelope",
                BodyEncryptionErrors.DecryptFailed(), bytes: request.ContentLength);

        if (empty) return;

        switch (settings.Mode)
        {
            case RequestEncryptionMode.Required when !IsPlainAllowed(settings, request.Path):
                throw Refuse(http, "plain body while RequestEncryption:Mode is Required", BodyEncryptionErrors.Required(),
                    bytes: request.ContentLength);

            case RequestEncryptionMode.Optional:
                // Counts the traffic from app builds older than 1.1.0; Required is safe once
                // these lines stop.
                logger.LogInformation("Plain request body accepted on {Method} {Path} (RequestEncryption:Mode is Optional)",
                    request.Method, request.Path);
                break;
        }
    }

    private static bool IsPlainAllowed(RequestEncryptionOptions settings, PathString path)
    {
        var value = path.Value ?? "";
        return settings.PlainAllowedPaths.Any(p =>
            string.Equals(p.TrimEnd('/'), value.TrimEnd('/'), StringComparison.OrdinalIgnoreCase));
    }

    private ApiException Refuse(HttpContext http, string reason, ApiException error,
        string? kid = null, long? bytes = null, string? part = null)
    {
        // Information: the global exception handler already writes the Warning for the refusal.
        logger.LogInformation(
            "Request body refused ({Code}) on {Method} {Path}: {Reason}; part {Part}, kid {Kid}, {Bytes} bytes",
            error.Code, http.Request.Method, http.Request.Path, reason, part ?? "-", kid ?? "-",
            bytes?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "?");
        return error;
    }

    /// <summary>
    /// Maps a failure while reading the body or the form:
    ///   client aborted          -> OperationCanceledException (the handler's Information path)
    ///   a size or form limit    -> 413 PAYLOAD_TOO_LARGE
    ///   other InvalidDataException (bad multipart framing) -> 400, not retryable
    ///   other IOException       -> 400, retryable
    /// </summary>
    private async Task<T> GuardRead<T>(HttpContext http, Func<Task<T>> read)
    {
        try
        {
            return await read();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e) when (e is IOException or InvalidDataException && http.RequestAborted.IsCancellationRequested)
        {
            throw new OperationCanceledException("The client closed the request.", http.RequestAborted);
        }
        catch (BadHttpRequestException e) when (e.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            throw Refuse(http, "request body larger than the server allows", BodyEncryptionErrors.TooLarge(),
                bytes: http.Request.ContentLength);
        }
        catch (Exception e) when (e is InvalidDataException or IOException && IsLimitExceeded(e))
        {
            throw Refuse(http, "a form limit was exceeded", BodyEncryptionErrors.TooLarge(),
                bytes: http.Request.ContentLength);
        }
        catch (InvalidDataException)
        {
            throw Refuse(http, "the form could not be parsed", BodyEncryptionErrors.FormUnreadable(),
                bytes: http.Request.ContentLength);
        }
        catch (IOException)
        {
            throw Refuse(http, "the body could not be read", BodyEncryptionErrors.BodyUnreadable(),
                bytes: http.Request.ContentLength);
        }
    }

    /// <summary>
    /// FormReader, MultipartReader and the request buffering stream report every limit (body
    /// length, value length, value count, key length, header length and count, buffer) as
    /// "... limit N exceeded." The one "limit" that is really a malformed request - an
    /// over-long multipart boundary in the Content-Type - stays a 400.
    /// </summary>
    private static bool IsLimitExceeded(Exception e) =>
        e.Message.Contains("limit", StringComparison.OrdinalIgnoreCase)
        && e.Message.Contains("exceeded", StringComparison.OrdinalIgnoreCase)
        && !e.Message.Contains("boundary length", StringComparison.OrdinalIgnoreCase);
}
