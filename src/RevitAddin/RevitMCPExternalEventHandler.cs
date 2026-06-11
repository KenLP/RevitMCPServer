using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCPAddin.Commands;

namespace RevitMCPAddin;

/// <summary>
/// Marshals incoming command requests onto the Revit main UI thread, and owns
/// the transaction lifecycle.
///
/// HTTP requests arrive on background threads inside <see cref="Server.McpHttpServer"/>.
/// They cannot touch the Revit API directly — Revit API calls must run inside an
/// ExternalEvent handler on the main thread.  We enqueue a <see cref="PendingRequest"/>
/// (carrying a <see cref="TaskCompletionSource{TResult}"/>), raise the ExternalEvent,
/// and the HTTP handler awaits the TCS for the result.
///
/// Transaction policy:
///   - For a single read-only command  → no transaction at all.
///   - For a single write command       → opens one Transaction named "MCP: &lt;cmd&gt;".
///   - For a batch                      → opens ONE Transaction named "MCP: Batch (n ops)"
///                                        and runs every sub-command inside it.  If any
///                                        sub-command throws and stopOnError is true the
///                                        transaction is rolled back.
/// </summary>
public sealed class RevitMCPExternalEventHandler : IExternalEventHandler
{
    private readonly CommandRegistry _registry;
    private readonly ConcurrentQueue<PendingRequest> _queue = new();
    private ExternalEvent? _externalEvent;

    public RevitMCPExternalEventHandler(CommandRegistry registry)
    {
        _registry = registry;
    }

    internal void AttachExternalEvent(ExternalEvent externalEvent)
    {
        _externalEvent = externalEvent;
    }

    public CommandRegistry Registry => _registry;

    /// <summary>Enqueue a single command call.</summary>
    public Task<JsonObject> EnqueueAsync(string commandName, JsonObject? parameters, bool dryRun = false)
    {
        var pending = new PendingRequest(
            kind: RequestKind.Single,
            commandName: commandName,
            parameters: parameters ?? new JsonObject(),
            steps: null,
            stopOnError: false,
            dryRun: dryRun);
        Enqueue(pending);
        return pending.Completion.Task;
    }

    /// <summary>Enqueue a batch (array of sub-commands inside one transaction).</summary>
    public Task<JsonObject> EnqueueBatchAsync(IList<BatchStep> steps, bool stopOnError, bool dryRun = false)
    {
        var pending = new PendingRequest(
            kind: RequestKind.Batch,
            commandName: "batch",
            parameters: new JsonObject(),
            steps: steps,
            stopOnError: stopOnError,
            dryRun: dryRun);
        Enqueue(pending);
        return pending.Completion.Task;
    }

    private void Enqueue(PendingRequest req)
    {
        _queue.Enqueue(req);
        // Raise() is documented as safe to call from any thread.
        _externalEvent?.Raise();
    }

    public void Execute(UIApplication app)
    {
        // Drain the whole queue in one Revit-thread tick.  Each top-level
        // request runs in its own try/catch so one bad request can't poison
        // the others sharing this tick.
        while (_queue.TryDequeue(out var req))
        {
            try
            {
                JsonObject result = req.Kind switch
                {
                    RequestKind.Single => RunSingle(app, req.CommandName, req.Parameters, req.DryRun),
                    RequestKind.Batch  => RunBatch(app, req.Steps!, req.StopOnError, req.DryRun),
                    _ => JsonResult.Error("internal", "Unknown request kind."),
                };
                req.Completion.SetResult(result);
            }
            catch (Exception ex)
            {
                req.Completion.SetResult(JsonResult.Error(
                    "command_failed",
                    ex.Message,
                    ex.GetType().FullName));
            }
        }
    }

    private JsonObject RunSingle(UIApplication app, string commandName, JsonObject parameters, bool dryRun)
    {
        if (!_registry.TryGet(commandName, out var command) || command is null)
            return JsonResult.Error("unknown_command",
                $"No command registered for '{commandName}'.");

        var ctx = BuildContext(app, parameters, dryRun);

        // Read-only and UI-action commands run WITHOUT a model transaction.
        if (command.Execution is ExecutionKind.ReadOnly or ExecutionKind.UiAction)
        {
            // A UI action cannot be previewed by rollback — there's no model
            // change to undo and the UI effect can't be reverted. In dry-run
            // we therefore report a no-op instead of mutating UI state.
            if (dryRun && command.Execution == ExecutionKind.UiAction)
            {
                return JsonResult.Success(new JsonObject
                {
                    ["dryRun"] = true,
                    ["committed"] = false,
                    ["skipped"] = true,
                    ["changeSummary"] =
                        $"Dry-run: UI action '{commandName}' not executed (UI changes cannot be rolled back).",
                });
            }

            try
            {
                var data = command.Execute(ctx);
                var result = JsonResult.Success(data);
                if (dryRun) result["dryRun"] = true;
                return result;
            }
            catch (RevitCommandException ex)
            {
                return JsonResult.Error(ex.Code, ex.Message);
            }
            catch (Exception ex)
            {
                return JsonResult.Error("command_failed", ex.Message, ex.GetType().FullName);
            }
        }

        // Model-write commands always need a document + a transaction.
        var doc = ctx.RequireDoc();
        using var tx = new Transaction(doc, $"MCP: {commandName}");
        try
        {
            tx.Start();
            var data = command.Execute(ctx);

            if (dryRun)
            {
                // Roll back — model is unchanged, but we still return the result
                // so the caller can preview what *would* have happened.
                if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                var result = JsonResult.Success(data);
                result["dryRun"] = true;
                result["committed"] = false;
                return result;
            }

            if (tx.HasStarted() && !tx.HasEnded())
                tx.Commit();
            return JsonResult.Success(data);
        }
        catch (RevitCommandException ex)
        {
            try { if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack(); } catch { }
            return JsonResult.Error(ex.Code, ex.Message);
        }
        catch (Exception ex)
        {
            try { if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack(); } catch { }
            return JsonResult.Error("command_failed", ex.Message, ex.GetType().FullName);
        }
    }

    private JsonObject RunBatch(UIApplication app, IList<BatchStep> steps, bool stopOnError, bool dryRun)
    {
        if (steps.Count == 0)
            return JsonResult.Error("bad_request", "Batch must contain at least one step.");

        // Resolve all handlers up-front and verify they exist.
        var resolved = new List<(BatchStep step, IRevitCommand cmd)>(steps.Count);
        var anyWrite = false;
        foreach (var step in steps)
        {
            if (!_registry.TryGet(step.CommandName, out var cmd) || cmd is null)
                return JsonResult.Error("unknown_command",
                    $"No command registered for '{step.CommandName}'.");
            resolved.Add((step, cmd));
            if (cmd.Execution == ExecutionKind.ModelWrite) anyWrite = true;
        }

        // Mixed batches are rejected: UI effects cannot be rolled back alongside
        // model changes, leading to unpredictable state on failure or dry-run.
        var mixedError = BatchPolicy.ValidateBatchKinds(resolved.Select(r => r.cmd.Execution));
        if (mixedError is not null) return mixedError;

        // No model writes (only read-only / UI actions): no transaction required.
        if (!anyWrite)
        {
            var roResults = new JsonArray();
            for (var i = 0; i < resolved.Count; i++)
            {
                var (step, cmd) = resolved[i];

                // Dry-run skips UI actions — they cannot be rolled back.
                if (dryRun && cmd.Execution == ExecutionKind.UiAction)
                {
                    var skipEnv = JsonResult.Success(new JsonObject
                    {
                        ["dryRun"] = true,
                        ["committed"] = false,
                        ["skipped"] = true,
                        ["changeSummary"] =
                            $"Dry-run: UI action '{step.CommandName}' not executed.",
                    });
                    skipEnv["index"] = i;
                    skipEnv["command"] = step.CommandName;
                    roResults.Add(skipEnv);
                    continue;
                }

                var ctx = BuildContext(app, step.Parameters, dryRun);
                var r = RunStepCaptured(cmd, ctx);
                r["index"] = i;
                r["command"] = step.CommandName;
                roResults.Add(r);
            }
            var roEnvelope = JsonResult.Success(new JsonObject
            {
                ["count"] = resolved.Count,
                ["results"] = roResults,
            });
            if (dryRun) roEnvelope["dryRun"] = true;
            return roEnvelope;
        }

        // Mixed / write batch: single transaction across the lot.
        var doc = app.ActiveUIDocument?.Document
            ?? throw new InvalidOperationException("Batch requires an active Revit document.");
        var results = new JsonArray();
        var hadFailure = false;

        using var tx = new Transaction(doc, $"MCP: Batch ({resolved.Count} ops)");
        tx.Start();
        try
        {
            for (var i = 0; i < resolved.Count; i++)
            {
                var (step, cmd) = resolved[i];
                var ctx = BuildContext(app, step.Parameters, dryRun);

                JsonObject stepEnvelope;
                try
                {
                    var data = cmd.Execute(ctx);
                    stepEnvelope = JsonResult.Success(data);
                }
                catch (RevitCommandException ex)
                {
                    hadFailure = true;
                    stepEnvelope = JsonResult.Error(ex.Code, ex.Message);
                    stepEnvelope["index"] = i;
                    stepEnvelope["command"] = step.CommandName;
                    results.Add(stepEnvelope);

                    if (stopOnError)
                    {
                        if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                        return new JsonObject
                        {
                            ["ok"] = false,
                            ["error"] = new JsonObject
                            {
                                ["code"] = "batch_aborted",
                                ["message"] = $"Batch aborted at step {i} ('{step.CommandName}'): {ex.Message}",
                            },
                            ["committed"] = false,
                            ["results"] = results,
                        };
                    }
                    continue;
                }
                catch (Exception ex)
                {
                    hadFailure = true;
                    stepEnvelope = JsonResult.Error("step_failed", ex.Message, ex.GetType().FullName);
                    stepEnvelope["index"] = i;
                    stepEnvelope["command"] = step.CommandName;
                    results.Add(stepEnvelope);

                    if (stopOnError)
                    {
                        if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                        return new JsonObject
                        {
                            ["ok"] = false,
                            ["error"] = new JsonObject
                            {
                                ["code"] = "batch_aborted",
                                ["message"] = $"Batch aborted at step {i} ('{step.CommandName}'): {ex.Message}",
                            },
                            ["committed"] = false,
                            ["results"] = results,
                        };
                    }
                    continue;
                }
                stepEnvelope["index"] = i;
                stepEnvelope["command"] = step.CommandName;
                results.Add(stepEnvelope);
            }

            if (tx.HasStarted() && !tx.HasEnded())
            {
                if (dryRun)
                    tx.RollBack();
                else
                    tx.Commit();
            }
        }
        catch
        {
            try { if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack(); } catch { }
            throw;
        }

        var batchResult = new JsonObject
        {
            ["ok"] = true,
            ["committed"] = !dryRun,
            ["count"] = resolved.Count,
            ["hadFailures"] = hadFailure,
            ["results"] = results,
        };
        if (dryRun) batchResult["dryRun"] = true;
        return batchResult;
    }

    private static JsonObject RunStepCaptured(IRevitCommand cmd, CommandContext ctx)
    {
        try
        {
            var data = cmd.Execute(ctx);
            return JsonResult.Success(data);
        }
        catch (RevitCommandException ex)
        {
            return JsonResult.Error(ex.Code, ex.Message);
        }
        catch (Exception ex)
        {
            return JsonResult.Error("step_failed", ex.Message, ex.GetType().FullName);
        }
    }

    private static CommandContext BuildContext(UIApplication app, JsonObject parameters, bool dryRun = false) => new()
    {
        App = app,
        Doc = app.ActiveUIDocument?.Document,
        Parameters = parameters,
        DryRun = dryRun,
    };

    public string GetName() => "RevitMCPExternalEventHandler";


    private enum RequestKind { Single, Batch }

    private sealed class PendingRequest
    {
        public RequestKind Kind { get; }
        public string CommandName { get; }
        public JsonObject Parameters { get; }
        public IList<BatchStep>? Steps { get; }
        public bool StopOnError { get; }
        public bool DryRun { get; }
        public TaskCompletionSource<JsonObject> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public PendingRequest(
            RequestKind kind,
            string commandName,
            JsonObject parameters,
            IList<BatchStep>? steps,
            bool stopOnError,
            bool dryRun = false)
        {
            Kind = kind;
            CommandName = commandName;
            Parameters = parameters;
            Steps = steps;
            StopOnError = stopOnError;
            DryRun = dryRun;
        }
    }
}

/// <summary>One step inside a batch request.</summary>
public sealed record BatchStep(string CommandName, JsonObject Parameters);
