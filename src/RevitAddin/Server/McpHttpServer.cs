using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using RevitMCPAddin.Commands;

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
    private readonly int _port;
    private readonly RevitMCPExternalEventHandler _handler;
    private readonly string? _authToken;
    private readonly HttpListener _listener = new();
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

            // Health endpoint is auth-exempt so clients can detect the addin.
            if (method == "GET" && path == "/health")
            {
                await WriteJsonAsync(response, 200, new JsonObject
                {
                    ["ok"] = true,
                    ["service"] = "revit-mcp-addin",
                    ["version"] = "0.4.0",
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
                foreach (var (name, isReadOnly, riskLevel) in _handler.Registry.Describe())
                {
                    arr.Add(new JsonObject
                    {
                        ["name"] = name,
                        ["isReadOnly"] = isReadOnly,
                        ["riskLevel"] = riskLevel,
                    });
                }
                await WriteJsonAsync(response, 200, JsonResult.Success(new JsonObject
                {
                    ["count"] = arr.Count,
                    ["commands"] = arr,
                })).ConfigureAwait(false);
                return;
            }

            if (method == "POST" && path == "/mcp")
            {
                await HandleSingleAsync(request, response).ConfigureAwait(false);
                return;
            }

            if (method == "POST" && path == "/mcp/batch")
            {
                await HandleBatchAsync(request, response).ConfigureAwait(false);
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

        JsonObject result;
        try
        {
            result = await _handler.EnqueueAsync(commandName, parameters, dryRun).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            result = JsonResult.Error("dispatch_failed", ex.Message, ex.GetType().FullName);
        }

        var statusCode = result["ok"]?.GetValue<bool>() == true ? 200 : 500;
        await WriteJsonAsync(response, statusCode, result).ConfigureAwait(false);
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

        var statusCode = result["ok"]?.GetValue<bool>() == true ? 200 : 500;
        await WriteJsonAsync(response, statusCode, result).ConfigureAwait(false);
    }

    private static async Task<JsonObject?> ReadJsonObjectAsync(HttpListenerRequest request, HttpListenerResponse response)
    {
        string body;
        using (var reader = new StreamReader(request.InputStream, request.ContentEncoding ?? Encoding.UTF8))
            body = await reader.ReadToEndAsync().ConfigureAwait(false);

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
        var bytes = Encoding.UTF8.GetBytes(body.ToJsonString());
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
    }
}
