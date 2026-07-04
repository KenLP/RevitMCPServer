using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using RevitMCPAddin.Commands;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("RevitMCPAddin.Tests")]

namespace RevitMCPAddin.Server;

/// <summary>
/// Tiny HttpListener-based JSON-RPC-ish server that runs inside the Revit
/// process.
///
///   POST /mcp           — single command  (supports ?dryRun=true)
///   POST /mcp/batch     — batch command   (supports ?dryRun=true or body.dryRun)
///   GET  /health
///   GET  /commands       — list every registered command + isReadOnly + riskLevel
///
/// Every response uses the envelope:
///   { ok: bool, data?: ..., error?: { code, message, type? } }
/// Bound to 127.0.0.1 only.
///
/// Auth: when an auth token is configured, every request must carry
///   Authorization: Bearer &lt;token&gt;
/// Health endpoint is exempt from auth so clients can detect the addin.
/// </summary>
public sealed class McpHttpServer
{
    // ── Limits / backpressure ────────────────────────────────────────────────
    private const long MaxRequestBytes = 1_048_576; // 1 MB request body cap
    private const int  MaxBatchSteps   = 200;        // steps per batch cap
    private const int  MaxInFlight     = 32;         // concurrent /mcp requests cap

    private readonly int _port;
    private readonly RevitMCPExternalEventHandler _handler;
    private readonly string? _authToken;
    private readonly HttpListener _listener = new();
    private readonly ServerMetrics _metrics = new();
    private CancellationTokenSource? _cts;
    private Task? _loopTask;

    public McpHttpServer(int port, RevitMCPExternalEventHandler handler, string? authToken = null)
    {
        _port = port;
        _handler = handler;
        _authToken = authToken;
        _listener.Prefixes.Add($"http://127.0.0.1:{_port}/");
    }

    /// <summary>The auth token in effect (null = auth disabled).</summary>
    public string? AuthToken => _authToken;

    public void Start()
    {
        _listener.Start();
        _cts = new CancellationTokenSource();
        _loopTask = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        try { _listener.Stop(); } catch { }
        try { _listener.Close(); } catch { }
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener.IsListening)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (HttpListenerException) { break; }
            catch (ObjectDisposedException) { break; }

            _ = Task.Run(() => HandleRequestAsync(ctx), ct);
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext ctx)
    {
        try
        {
            var request = ctx.Request;
            var response = ctx.Response;
            response.ContentType = "application/json; charset=utf-8";

            var path = request.Url?.AbsolutePath ?? "";
            var method = request.HttpMethod;

            // Correlation id: honour a client-supplied X-Request-Id, else mint one.
            // Echoed on the response and threaded into the request log.
            var requestId = request.Headers["X-Request-Id"];
            if (string.IsNullOrWhiteSpace(requestId))
                requestId = Guid.NewGuid().ToString("N").Substring(0, 12);
            try { response.Headers["X-Request-Id"] = requestId; } catch { }

            // Health endpoint is auth-exempt so clients can detect the addin.
            if (method == "GET" && path == "/health")
            {
                await WriteJsonAsync(response, 200, new JsonObject
                {
                    ["ok"] = true,
                    ["service"] = "revit-mcp-addin",
                    ["version"] = "0.8.13",
                    ["authEnabled"] = _authToken is not null,
                }).ConfigureAwait(false);
                return;
            }

            // Auth check for all other endpoints.
            if (!CheckAuth(request, response))
            {
                await WriteJsonAsync(response, 401,
                    JsonResult.Error("unauthorized",
                        "Missing or invalid Authorization header. Expected: Bearer <token>"))
                    .ConfigureAwait(false);
                return;
            }

            if (method == "GET" && path == "/commands")
            {
                var arr = new JsonArray();
                foreach (var (name, isReadOnly, riskLevel, executionKind) in _handler.Registry.Describe())
                {
                    arr.Add(new JsonObject
                    {
                        ["name"] = name,
                        ["isReadOnly"] = isReadOnly,
                        ["riskLevel"] = riskLevel,
                        ["executionKind"] = executionKind,
                    });
                }
                await WriteJsonAsync(response, 200, JsonResult.Success(new JsonObject
                {
                    ["count"] = arr.Count,
                    ["commands"] = arr,
                })).ConfigureAwait(false);
                return;
            }

            if (method == "GET" && path == "/stats")
            {
                await WriteJsonAsync(response, 200, JsonResult.Success(_metrics.Snapshot()))
                    .ConfigureAwait(false);
                return;
            }

            if (method == "POST" && (path == "/mcp" || path == "/mcp/batch"))
            {
                // Backpressure: shed load instead of unbounded queue growth.
                if (_metrics.InFlight >= MaxInFlight)
                {
                    _metrics.RecordRejected();
                    try { response.Headers["Retry-After"] = "1"; } catch { }
                    await WriteJsonAsync(response, 503,
                        JsonResult.Error("overloaded",
                            $"Server busy ({_metrics.InFlight} requests in flight). Retry shortly."))
                        .ConfigureAwait(false);
                    return;
                }

                _metrics.IncInFlight();
                var sw = Stopwatch.StartNew();
                try
                {
                    if (path == "/mcp")
                        await HandleSingleAsync(request, response).ConfigureAwait(false);
                    else
                        await HandleBatchAsync(request, response).ConfigureAwait(false);
                }
                finally
                {
                    sw.Stop();
                    _metrics.DecInFlight();
                    var ok = response.StatusCode == 200;
                    _metrics.Record(ok, sw.ElapsedMilliseconds);
                    RequestLog.Write(new JsonObject
                    {
                        ["ts"] = DateTime.Now.ToString("o"),
                        ["requestId"] = requestId,
                        ["method"] = method,
                        ["path"] = path,
                        ["status"] = response.StatusCode,
                        ["ok"] = ok,
                        ["durationMs"] = sw.ElapsedMilliseconds,
                        ["inFlight"] = _metrics.InFlight,
                    });
                }
                return;
            }

            await WriteJsonAsync(response, 404,
                JsonResult.Error("not_found", $"No route for {method} {path}"))
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            try
            {
                await WriteJsonAsync(ctx.Response, 500,
                    JsonResult.Error("server_error", ex.Message, ex.GetType().FullName))
                    .ConfigureAwait(false);
            }
            catch { /* swallow */ }
        }
        finally
        {
            try { ctx.Response.OutputStream.Close(); } catch { }
        }
    }

    /// <summary>Returns true if auth passes (token matches or auth is disabled).</summary>
    private bool CheckAuth(HttpListenerRequest request, HttpListenerResponse response)
    {
        if (_authToken is null) return true; // auth disabled

        var header = request.Headers["Authorization"];
        if (string.IsNullOrWhiteSpace(header)) return false;

        // Expect "Bearer <token>"
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return false;
        var token = header.Substring(7).Trim();
        return string.Equals(token, _authToken, StringComparison.Ordinal);
    }

    /// <summary>
    /// Map a result envelope to an HTTP status code.  Success → 200.  For
    /// failures, the error <c>code</c> determines the status so clients can
    /// distinguish user/client errors from genuine server faults.
    /// </summary>
    internal static int StatusForResult(JsonObject result)
    {
        if (result["ok"]?.GetValue<bool>() == true)
            return 200;

        var code = (result["error"] as JsonObject)?["code"]?.GetValue<string>();
        return code switch
        {
            "bad_request" or "bad_json"
              or "invalid_parameter" or "read_only_parameter"
              or "unsupported_view" or "invalid_chars"
              or "too_many_steps"
              or "wrong_element_type"                         => 400,
            "unauthorized"                        => 401,
            "unknown_command" or "not_found"      => 404,
            "timeout" or "cancelled"              => 408,
            "name_collision" or "system_family"
              or "ambiguous_selection"            => 409,
            "payload_too_large"                   => 413,
            "overloaded"                          => 503,
            // command_failed / step_failed / dispatch_failed / server_error /
            // batch_aborted and anything unrecognised fall through to 500.
            _                                     => 500,
        };
    }

    /// <summary>Parse dryRun from query string (?dryRun=true) or JSON body field.</summary>
    private static bool ParseDryRun(HttpListenerRequest request, JsonObject? body)
    {
        // Query string takes precedence
        var qs = request.QueryString["dryRun"];
        if (qs is not null)
            return string.Equals(qs, "true", StringComparison.OrdinalIgnoreCase);

        // Fall back to body field
        return body?["dryRun"]?.GetValue<bool>() ?? false;
    }

    private async Task HandleSingleAsync(HttpListenerRequest request, HttpListenerResponse response)
    {
        var envelope = await ReadJsonObjectAsync(request, response).ConfigureAwait(false);
        if (envelope is null) return;

        if (envelope["command"] is not JsonNode cmdNode)
        {
            await WriteJsonAsync(response, 400,
                JsonResult.Error("bad_request",
                    "Body must be a JSON object with a 'command' field."))
                .ConfigureAwait(false);
            return;
        }

        var commandName = cmdNode.GetValue<string>();
        var parameters = envelope["params"] as JsonObject;
        var dryRun = ParseDryRun(request, envelope);

        // "batch" is not a registered IRevitCommand — it is a transport-level
        // dispatch primitive.  When a client posts  POST /mcp {command:"batch"}
        // (e.g. bim-orchestrator strips the revit_ prefix from revit_batch),
        // we parse the steps from params and delegate to the batch handler so
        // that both POST /mcp (with command:"batch") and POST /mcp/batch are
        // equivalent.  This closes the stdio ↔ HTTP parity gap.
        if (string.Equals(commandName, "batch", StringComparison.OrdinalIgnoreCase))
        {
            await HandleSingleAsBatchAsync(request, response, parameters, dryRun).ConfigureAwait(false);
            return;
        }

        JsonObject result;
        try
        {
            result = await _handler.EnqueueAsync(commandName, parameters, dryRun).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            result = JsonResult.Error("dispatch_failed", ex.Message, ex.GetType().FullName);
        }

        await WriteJsonAsync(response, StatusForResult(result), result).ConfigureAwait(false);
    }

    /// <summary>
    /// Handles POST /mcp {command:"batch", params:{steps:[...], stopOnError?:bool}}
    /// by parsing the steps from params and routing to <see cref="HandleBatchAsync"/>.
    /// Keeps stdio ↔ HTTP-direct parity: the orchestrator strips the revit_ prefix
    /// and always posts to /mcp, never to /mcp/batch.
    /// </summary>
    private async Task HandleSingleAsBatchAsync(
        HttpListenerRequest request,
        HttpListenerResponse response,
        JsonObject? parameters,
        bool dryRun)
    {
        var (steps, stopOnError, error) = ParseBatchParams(parameters);
        if (error is not null)
        {
            await WriteJsonAsync(response, 400,
                JsonResult.Error("bad_request", error))
                .ConfigureAwait(false);
            return;
        }

        JsonObject result;
        try
        {
            result = await _handler.EnqueueBatchAsync(steps!, stopOnError, dryRun).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            result = JsonResult.Error("dispatch_failed", ex.Message, ex.GetType().FullName);
        }

        await WriteJsonAsync(response, StatusForResult(result), result).ConfigureAwait(false);
    }

    /// <summary>
    /// Parses the steps array and stopOnError flag from the <c>params</c> object of a
    /// POST /mcp {command:"batch"} request.  Exposed as <c>internal</c> for unit testing
    /// without spinning up an HttpListener.
    /// Returns (steps, stopOnError, null) on success, or (null, _, errorMessage) on failure.
    /// </summary>
    internal static (List<BatchStep>? Steps, bool StopOnError, string? Error)
        ParseBatchParams(JsonObject? parameters)
    {
        if (parameters?["steps"] is not JsonArray stepsArray)
            return (null, true, "Batch command requires params.steps array.");

        if (stepsArray.Count > MaxBatchSteps)
            return (null, true, $"Batch has {stepsArray.Count} steps; the maximum is {MaxBatchSteps}.");

        var stopOnError = parameters["stopOnError"]?.GetValue<bool>() ?? true;
        var steps = new List<BatchStep>(stepsArray.Count);

        foreach (var node in stepsArray)
        {
            if (node is not JsonObject step)
                return (null, stopOnError, "Each step must be an object.");

            var name = step["command"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(name))
                return (null, stopOnError, "Each step must have a 'command' field.");

            var paramsObj = step["params"] as JsonObject;
            var detachedParams = paramsObj is null
                ? new JsonObject()
                : JsonNode.Parse(paramsObj.ToJsonString())!.AsObject();

            steps.Add(new BatchStep(name!, detachedParams));
        }

        return (steps, stopOnError, null);
    }

    private async Task HandleBatchAsync(HttpListenerRequest request, HttpListenerResponse response)
    {
        var envelope = await ReadJsonObjectAsync(request, response).ConfigureAwait(false);
        if (envelope is null) return;

        if (envelope["steps"] is not JsonArray stepsArray)
        {
            await WriteJsonAsync(response, 400,
                JsonResult.Error("bad_request",
                    "Body must contain a 'steps' array."))
                .ConfigureAwait(false);
            return;
        }

        if (stepsArray.Count > MaxBatchSteps)
        {
            await WriteJsonAsync(response, 400,
                JsonResult.Error("too_many_steps",
                    $"Batch has {stepsArray.Count} steps; the maximum is {MaxBatchSteps}."))
                .ConfigureAwait(false);
            return;
        }

        var stopOnError = envelope["stopOnError"]?.GetValue<bool>() ?? true;
        var dryRun = ParseDryRun(request, envelope);

        var steps = new List<BatchStep>(stepsArray.Count);
        foreach (var node in stepsArray)
        {
            if (node is not JsonObject step)
            {
                await WriteJsonAsync(response, 400,
                    JsonResult.Error("bad_request", "Each step must be an object."))
                    .ConfigureAwait(false);
                return;
            }

            var name = step["command"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(name))
            {
                await WriteJsonAsync(response, 400,
                    JsonResult.Error("bad_request", "Each step must have a 'command' field."))
                    .ConfigureAwait(false);
                return;
            }

            // Detach the params object from its parent so the dispatcher can re-parent it.
            var paramsObj = (step["params"] as JsonObject);
            JsonObject detachedParams;
            if (paramsObj is null)
            {
                detachedParams = new JsonObject();
            }
            else
            {
                detachedParams = JsonNode.Parse(paramsObj.ToJsonString())!.AsObject();
            }
            steps.Add(new BatchStep(name!, detachedParams));
        }

        JsonObject result;
        try
        {
            result = await _handler.EnqueueBatchAsync(steps, stopOnError, dryRun).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            result = JsonResult.Error("dispatch_failed", ex.Message, ex.GetType().FullName);
        }

        await WriteJsonAsync(response, StatusForResult(result), result).ConfigureAwait(false);
    }

    private static async Task<JsonObject?> ReadJsonObjectAsync(HttpListenerRequest request, HttpListenerResponse response)
    {
        // Reject oversized bodies up-front when the client declares Content-Length.
        if (request.ContentLength64 > MaxRequestBytes)
        {
            await WriteJsonAsync(response, 413,
                JsonResult.Error("payload_too_large",
                    $"Request body {request.ContentLength64} bytes exceeds the {MaxRequestBytes}-byte limit."))
                .ConfigureAwait(false);
            return null;
        }

        // JSON is canonically UTF-8 (RFC 8259). HttpListenerRequest.ContentEncoding
        // falls back to Encoding.Default when Content-Type omits charset, which has
        // bitten us with mojibake (em-dash, §) on requests where the client sent
        // valid UTF-8 bytes. Force UTF-8 on the read path.
        string body;
        using (var reader = new StreamReader(request.InputStream, Encoding.UTF8))
            body = await reader.ReadToEndAsync().ConfigureAwait(false);

        // Backstop for chunked requests (Content-Length unknown / -1).
        if (Encoding.UTF8.GetByteCount(body) > MaxRequestBytes)
        {
            await WriteJsonAsync(response, 413,
                JsonResult.Error("payload_too_large",
                    $"Request body exceeds the {MaxRequestBytes}-byte limit."))
                .ConfigureAwait(false);
            return null;
        }

        try
        {
            if (JsonNode.Parse(body) is JsonObject obj) return obj;
            await WriteJsonAsync(response, 400,
                JsonResult.Error("bad_request", "Body must be a JSON object.")).ConfigureAwait(false);
            return null;
        }
        catch (JsonException ex)
        {
            await WriteJsonAsync(response, 400,
                JsonResult.Error("bad_json", ex.Message)).ConfigureAwait(false);
            return null;
        }
    }

    private static async Task WriteJsonAsync(HttpListenerResponse response, int statusCode, JsonObject body)
    {
        response.StatusCode = statusCode;
        response.ContentEncoding = Encoding.UTF8;
        var bytes = Encoding.UTF8.GetBytes(body.ToJsonString());
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
    }
}
