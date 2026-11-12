using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using PummelPartyMcp.Core;

namespace PummelPartyMcp.Tests
{
    /// <summary>Phase 2 increment 4: run_batch (plan, placeholders, runner, registry), playtest flow/options/clock, validator report, guard in play states.</summary>
    internal static class PlaytestBatchTests
    {
        private static Action<string, bool> s_check;

        public static void Run(Action<string, bool> check)
        {
            s_check = check;
            PlanTests();
            PlaceholderTests();
            RunnerTests();
            RegistryBatchTests();
            PlayStateGuardTests();
            StartFlowTests();
            StopFlowTests();
            OptionsTests();
            ClockTests();
            ReportTests();
            MiscTests();
        }

        private static void Check(string name, bool ok) => s_check(name, ok);

        private static string Code(Action a)
        {
            try { a(); return null; }
            catch (RpcException e) { return e.Code; }
            catch (Exception e) { return e.GetType().Name; }
        }

        private static string Message(Action a)
        {
            try { a(); return null; }
            catch (Exception e) { return e.Message; }
        }

        // ================================================================== plan

        private static readonly HashSet<string> s_registeredWrite = new HashSet<string>(BatchRules.AllowedWrite.Concat(new[] { "run_batch", "create_test_mod", "open_test_mod", "start_playtest", "stop_playtest" }));
        private static readonly HashSet<string> s_registeredRead = new HashSet<string>(BatchRules.AllowedRead.Concat(new[] { "ping", "get_status", "get_log", "screenshot", "get_open_mod", "validate_mod" }));

        private static BatchToolInfo Lookup(string name)
        {
            if (s_registeredWrite.Contains(name))
            {
                return new BatchToolInfo
                {
                    IsWrite = true, Timeout = TimeSpan.FromSeconds(15),
                    Parse = p =>
                    {
                        if (p["bad"] != null)
                        {
                            throw RpcException.InvalidParams("'bad' is not allowed");
                        }
                        return p;
                    },
                };
            }
            if (s_registeredRead.Contains(name))
            {
                return new BatchToolInfo { IsWrite = false, Timeout = TimeSpan.FromSeconds(10) };
            }
            return null;
        }

        private static JObject Batch(params JObject[] steps) => new JObject { ["steps"] = new JArray(steps) };

        private static JObject Step(string tool, JObject ps = null, string id = null)
        {
            JObject s = new JObject { ["tool"] = tool };
            if (ps != null) s["params"] = ps;
            if (id != null) s["id"] = id;
            return s;
        }

        private static string PlanCode(JObject p) => Code(() => BatchPlan.Parse(p, Lookup));

        private static void PlanTests()
        {
            Console.WriteLine("-- unit: run_batch plan (up-front validation)");
            BatchPlan ok = BatchPlan.Parse(Batch(Step("create_object", new JObject { ["kind"] = "cube" }, "floor"),
                Step("set_transform", new JObject { ["guid"] = "$floor.guid" }), Step("get_hierarchy")), Lookup);
            Check("valid plan: 3 steps, stopOnError default true, ids/placeholder flags/write flags",
                ok.Steps.Count == 3 && ok.StopOnError && ok.Steps[0].Id == "floor" && !ok.Steps[0].HasPlaceholders && ok.Steps[1].HasPlaceholders
                && ok.Steps[0].IsWrite && !ok.Steps[2].IsWrite && ok.Steps[0].PreParsed && !ok.Steps[1].PreParsed && ok.Steps[0].Timeout == TimeSpan.FromSeconds(15));
            JObject cont = Batch(Step("save_mod")); cont["stopOnError"] = false;
            Check("stopOnError false accepted", !BatchPlan.Parse(cont, Lookup).StopOnError);
            JObject badStop = Batch(Step("save_mod")); badStop["stopOnError"] = "no";
            Check("stopOnError non-bool refused", PlanCode(badStop) == ErrorCodes.InvalidParams);
            Check("missing steps refused", PlanCode(new JObject()) == ErrorCodes.InvalidParams);
            Check("empty steps refused", PlanCode(Batch()) == ErrorCodes.InvalidParams);
            Check("101 steps refused", PlanCode(Batch(Enumerable.Range(0, 101).Select(_ => Step("save_mod")).ToArray())) == ErrorCodes.InvalidParams);
            Check("100 steps accepted", PlanCode(Batch(Enumerable.Range(0, 100).Select(_ => Step("save_mod")).ToArray())) == null);
            Check("non-object step refused", PlanCode(new JObject { ["steps"] = new JArray("save_mod") }) == ErrorCodes.InvalidParams);
            JObject extra = Step("save_mod"); extra["param"] = new JObject();
            Check("unknown step key refused", PlanCode(Batch(extra)) == ErrorCodes.InvalidParams);
            Check("missing tool refused", PlanCode(Batch(new JObject { ["params"] = new JObject() })) == ErrorCodes.InvalidParams);
            Check("params not an object refused", PlanCode(Batch(new JObject { ["tool"] = "save_mod", ["params"] = new JArray() })) == ErrorCodes.InvalidParams);
            string unk = Message(() => BatchPlan.Parse(Batch(Step("save_mod"), Step("frobnicate")), Lookup));
            Check("unknown tool refused up front, message names steps[1] and says nothing was run", unk != null && unk.Contains("steps[1]") && unk.Contains("unknown tool") && unk.Contains("nothing was run"));
            foreach (string dis in new[] { "run_batch", "create_test_mod", "open_test_mod", "start_playtest", "stop_playtest", "screenshot" })
            {
                string m = Message(() => BatchPlan.Parse(Batch(Step("save_mod"), Step(dis)), Lookup));
                Check($"disallowed tool '{dis}' refused up front naming the step", m != null && m.Contains("steps[1]") && m.Contains("not allowed inside run_batch"));
            }
            foreach (string notListed in new[] { "get_status", "get_log", "ping", "get_open_mod", "validate_mod" })
            {
                Check($"read tool '{notListed}' outside the allow list refused", PlanCode(Batch(Step(notListed))) == ErrorCodes.InvalidParams);
            }
            Check("duplicate id refused", PlanCode(Batch(Step("save_mod", null, "a"), Step("save_mod", null, "a"))) == ErrorCodes.InvalidParams);
            Check("bad id refused (starts with digit)", PlanCode(Batch(Step("save_mod", null, "1a"))) == ErrorCodes.InvalidParams);
            Check("bad id refused (space)", PlanCode(Batch(Step("save_mod", null, "a b"))) == ErrorCodes.InvalidParams);
            string parseErr = Message(() => BatchPlan.Parse(Batch(Step("save_mod"), Step("set_object", new JObject { ["bad"] = 1 })), Lookup));
            Check("write step params parsed up front: tool's own INVALID_PARAMS refuses the batch naming the step", parseErr != null && parseErr.Contains("steps[1] (set_object)") && parseErr.Contains("'bad'"));
            Check("write step with placeholders is not parsed up front (parsed when it runs)",
                PlanCode(Batch(Step("create_object", null, "a"), Step("set_object", new JObject { ["bad"] = 1, ["guid"] = "$a.guid" }))) == null);
        }

        // ================================================================== placeholders

        private static void PlaceholderTests()
        {
            Console.WriteLine("-- unit: run_batch placeholders");
            Check("forward reference refused up front",
                Message(() => BatchPlan.Parse(Batch(Step("set_object", new JObject { ["guid"] = "$b.guid" }), Step("create_object", null, "b")), Lookup))?.Contains("earlier step") == true);
            Check("unknown id refused up front", PlanCode(Batch(Step("create_object", null, "a"), Step("set_object", new JObject { ["guid"] = "$zz.guid" }))) == ErrorCodes.InvalidParams);
            Check("self reference refused", PlanCode(Batch(Step("set_object", new JObject { ["guid"] = "$a.guid" }, "a"))) == ErrorCodes.InvalidParams);
            foreach (string bad in new[] { "$a", "$a.", "$a..guid", "$a.guid[x]", "$a[01]", "$a[10000]", "$1.guid", "$a.9x", "$a.guid ", "$ a.guid", "$a-.g-h",
                                           "$a.b.c.d.e.f.g.h.i.j" })
            {
                Check($"disallowed path refused: '{bad}'", PlanCode(Batch(Step("create_object", null, "a"), Step("set_object", new JObject { ["name"] = bad }))) == ErrorCodes.InvalidParams);
            }
            Check("8 accessors allowed", PlanCode(Batch(Step("create_object", null, "a"), Step("set_object", new JObject { ["name"] = "$a.b.c.d.e.f.g[0].h" }))) == null);
            Check("ids may contain - and _", PlanCode(Batch(Step("create_object", null, "my-floor_1"), Step("set_object", new JObject { ["guid"] = "$my-floor_1.guid" }))) == null);
            Check("'$$' is an escape, not a reference", PlanCode(Batch(Step("set_object", new JObject { ["name"] = "$$5 prize" }))) == null);
            Check("object keys are never references", PlanCode(Batch(Step("set_properties", new JObject { ["values"] = new JObject { ["$weird"] = 1 } }))) == null);

            JObject result = new JObject
            {
                ["guid"] = "g-1",
                ["objects"] = new JArray(new JObject { ["guid"] = "c-1" }, new JObject { ["guid"] = "c-2" }),
                ["transform"] = new JObject { ["localPosition"] = new JObject { ["x"] = 1, ["y"] = 2, ["z"] = 3 } },
                ["n"] = 5,
            };
            Func<string, JToken> resultOf = id => id == "a" ? result : throw RpcException.InvalidParams($"step '{id}' failed");
            JObject input = new JObject
            {
                ["guid"] = "$a.guid",
                ["nested"] = new JObject { ["list"] = new JArray("$a.objects[1].guid", 7, new JArray("$a.n"), "plain") },
                ["pos"] = "$a.transform.localPosition",
                ["lit"] = "$$a.guid",
                ["text"] = "costs $ 5",
            };
            JObject resolved = Placeholders.Resolve(input, resultOf);
            Check("resolve: top-level, nested arrays/objects, whole-object values, escape, untouched strings",
                (string)resolved["guid"] == "g-1" && (string)resolved["nested"]["list"][0] == "c-2" && (int)resolved["nested"]["list"][1] == 7
                && (int)resolved["nested"]["list"][2][0] == 5 && (string)resolved["nested"]["list"][3] == "plain"
                && (int)resolved["pos"]["z"] == 3 && (string)resolved["lit"] == "$a.guid" && (string)resolved["text"] == "costs $ 5");
            Check("resolve returns a copy (input unchanged; result value cloned)", (string)input["guid"] == "$a.guid" && !ReferenceEquals(resolved["pos"], result["transform"]["localPosition"]));
            Check("missing member -> INVALID_PARAMS", Code(() => Placeholders.Resolve(new JObject { ["g"] = "$a.nope" }, resultOf)) == ErrorCodes.InvalidParams);
            Check("index out of range -> INVALID_PARAMS with count", Message(() => Placeholders.Resolve(new JObject { ["g"] = "$a.objects[5].guid" }, resultOf))?.Contains("it has 2") == true);
            Check("member access on a scalar -> INVALID_PARAMS", Code(() => Placeholders.Resolve(new JObject { ["g"] = "$a.n.x" }, resultOf)) == ErrorCodes.InvalidParams);
            Check("reference to a failed step -> resolver's INVALID_PARAMS", Code(() => Placeholders.Resolve(new JObject { ["g"] = "$b.guid" }, resultOf)) == ErrorCodes.InvalidParams);
            Check("array result addressed by index ($a[0])", (string)Placeholders.Resolve(new JObject { ["t"] = "$a[0].triggerName" },
                id => new JArray(new JObject { ["triggerName"] = "Triggers[0]" }))["t"] == "Triggers[0]");
        }

        // ================================================================== runner

        private sealed class OuterCompletion : IWorkCompletion
        {
            public Func<TimeSpan> Clock;
            public TimeSpan Deadline;
            public bool Cancelled;
            public bool Committed;
            public object Result;
            public Exception Error;
            public int Completions;
            public bool IsCancelled => Cancelled;
            public bool TryCommit() { if (Cancelled) return false; Committed = true; return true; }
            public TimeSpan Remaining => Deadline - Clock();
            public void SetResult(object result) { Result = result; Completions++; }
            public void SetException(Exception error) { Error = error; Completions++; }
        }

        /// <summary>Fake step executor: behaviour per tool name.</summary>
        private sealed class FakeExecutor : IBatchStepExecutor
        {
            public readonly List<string> Started = new List<string>();
            public readonly Dictionary<int, IWorkCompletion> Pending = new Dictionary<int, IWorkCompletion>();
            public readonly List<JObject> ParamsSeen = new List<JObject>();
            public Action<BatchStep, IWorkCompletion> Custom;

            public void Start(BatchStep step, JObject resolvedParams, IWorkCompletion c)
            {
                Started.Add(step.Tool);
                ParamsSeen.Add(resolvedParams);
                if (Custom != null)
                {
                    Custom(step, c);
                    return;
                }
                switch (step.Tool)
                {
                    case "create_object":
                        if (!c.TryCommit()) { c.SetException(new RpcException(ErrorCodes.Timeout, "cancelled")); return; }
                        c.SetResult(new JObject { ["guid"] = "guid-" + step.Index });
                        break;
                    case "set_object": // fails when params say so
                        if (resolvedParams?["fail"] != null) { c.SetException(new RpcException(ErrorCodes.NotFound, "no such object")); return; }
                        c.TryCommit();
                        c.SetResult(new JObject { ["guid"] = resolvedParams?["guid"] });
                        break;
                    case "set_transform": // asynchronous: completed by the test
                        Pending[step.Index] = c;
                        break;
                    case "remove_component":
                        throw new InvalidOperationException("boom");
                    default:
                        c.SetResult(new JObject { ["tool"] = step.Tool });
                        break;
                }
            }
        }

        private static BatchPlan Plan(bool stopOnError, params JObject[] steps)
        {
            JObject p = Batch(steps);
            p["stopOnError"] = stopOnError;
            return BatchPlan.Parse(p, Lookup);
        }

        private static void RunnerTests()
        {
            Console.WriteLine("-- unit: run_batch runner (fake tools, fake clock)");
            TimeSpan now = TimeSpan.Zero;
            Func<TimeSpan> clock = () => now;

            // all succeed, placeholders resolved from earlier results
            OuterCompletion o1 = new OuterCompletion { Clock = clock, Deadline = TimeSpan.FromSeconds(120) };
            FakeExecutor e1 = new FakeExecutor();
            BatchRunner r1 = new BatchRunner(Plan(true, Step("create_object", null, "floor"), Step("set_object", new JObject { ["guid"] = "$floor.guid" }), Step("get_hierarchy")), o1, e1, clock);
            bool done1 = r1.Start();
            JObject res1 = o1.Result as JObject;
            Check("all steps succeed synchronously: one completion, completed 3, no failedStep, not stopped early",
                done1 && o1.Completions == 1 && (int)res1["completed"] == 3 && res1["failedStep"] == null && !(bool)res1["stoppedEarly"] && res1["stopReason"] == null);
            Check("placeholder resolved from the earlier step's result", (string)e1.ParamsSeen[1]["guid"] == "guid-0" && (string)res1["results"][1]["result"]["guid"] == "guid-0");
            Check("result rows carry index, id, tool, ok", (int)res1["results"][0]["index"] == 0 && (string)res1["results"][0]["id"] == "floor" && (string)res1["results"][0]["tool"] == "create_object" && (bool)res1["results"][0]["ok"]);
            Check("first step commit commits the outer call", o1.Committed);
            Check("note says there is no rollback", ((string)res1["note"]).Contains("no rollback"));

            // stopOnError true
            OuterCompletion o2 = new OuterCompletion { Clock = clock, Deadline = TimeSpan.FromSeconds(120) };
            FakeExecutor e2 = new FakeExecutor();
            new BatchRunner(Plan(true, Step("create_object"), Step("set_object", new JObject { ["fail"] = true }), Step("create_object")), o2, e2, clock).Start();
            JObject res2 = (JObject)o2.Result;
            Check("stopOnError: stops at the failing step, later steps not started",
                e2.Started.Count == 2 && (int)res2["completed"] == 1 && (int)res2["failedStep"] == 1 && (bool)res2["stoppedEarly"] && (string)res2["stopReason"] == "stepFailed"
                && (string)res2["results"][1]["error"]["code"] == ErrorCodes.NotFound && ((JArray)res2["results"]).Count == 2);

            // continue mode + dependent reference to failed step
            OuterCompletion o3 = new OuterCompletion { Clock = clock, Deadline = TimeSpan.FromSeconds(120) };
            FakeExecutor e3 = new FakeExecutor();
            new BatchRunner(Plan(false, Step("set_object", new JObject { ["fail"] = true }, "x"), Step("set_object", new JObject { ["guid"] = "$x.guid" }), Step("create_object")), o3, e3, clock).Start();
            JObject res3 = (JObject)o3.Result;
            Check("continue mode: every step attempted, failedStep = first failure, completed counts successes",
                (int)res3["attempted"] == 3 && (int)res3["completed"] == 1 && (int)res3["failedStep"] == 0 && !(bool)res3["stoppedEarly"]);
            Check("reference to a failed step fails that step with INVALID_PARAMS without starting it",
                (string)res3["results"][1]["error"]["code"] == ErrorCodes.InvalidParams && e3.Started.Count == 2);

            // throwing executor
            OuterCompletion o4 = new OuterCompletion { Clock = clock, Deadline = TimeSpan.FromSeconds(120) };
            new BatchRunner(Plan(true, Step("remove_component", new JObject())), o4, new FakeExecutor(), clock).Start();
            Check("a step that throws is recorded as a failed step (INTERNAL)", (string)((JObject)o4.Result)["results"][0]["error"]["code"] == ErrorCodes.Internal);

            // async step across ticks
            OuterCompletion o5 = new OuterCompletion { Clock = clock, Deadline = TimeSpan.FromSeconds(120) };
            FakeExecutor e5 = new FakeExecutor();
            BatchRunner r5 = new BatchRunner(Plan(true, Step("set_transform", new JObject()), Step("create_object")), o5, e5, clock);
            Check("async step: runner waits", !r5.Start() && o5.Completions == 0 && e5.Started.Count == 1);
            Check("async step: still waiting on the next tick", !r5.Tick() && e5.Started.Count == 1);
            e5.Pending[0].TryCommit();
            e5.Pending[0].SetResult(new JObject { ["ok"] = 1 });
            Check("async step completed: next tick records it and runs the rest", r5.Tick() && e5.Started.Count == 2 && (int)((JObject)o5.Result)["completed"] == 2);

            // time budget: a step is not started when < 4 s are left
            now = TimeSpan.Zero;
            OuterCompletion o6 = new OuterCompletion { Clock = clock, Deadline = TimeSpan.FromSeconds(10) };
            FakeExecutor e6 = new FakeExecutor();
            BatchRunner r6 = new BatchRunner(Plan(true, Step("set_transform", new JObject()), Step("create_object")), o6, e6, clock);
            r6.Start();
            now = TimeSpan.FromSeconds(5.5); // 10 - 1 margin - 5.5 = 3.5 s left
            e6.Pending[0].TryCommit();
            e6.Pending[0].SetResult(new JObject());
            r6.Tick();
            JObject res6 = (JObject)o6.Result;
            Check("time budget: step 1 not started with 3.5 s left; partial result, stopReason timeBudget",
                e6.Started.Count == 1 && (int)res6["completed"] == 1 && (bool)res6["stoppedEarly"] && (string)res6["stopReason"] == "timeBudget" && res6["failedStep"] == null);
            Check("time budget note says later steps were not started", ((string)res6["note"]).Contains("not started"));

            // step deadline capped by batch deadline; pre-commit cancellation at the deadline
            now = TimeSpan.Zero;
            OuterCompletion o7 = new OuterCompletion { Clock = clock, Deadline = TimeSpan.FromSeconds(8) };
            IWorkCompletion held = null;
            FakeExecutor e7 = new FakeExecutor { Custom = (s, c) => held = c };
            BatchRunner r7 = new BatchRunner(Plan(true, Step("set_object", new JObject())), o7, e7, clock);
            r7.Start();
            Check("step Remaining = min(tool limit 15 s, batch deadline 8 s - 1 s margin)", Math.Abs(held.Remaining.TotalSeconds - 7) < 0.001);
            now = TimeSpan.FromSeconds(7.1);
            Check("before commit, the step's deadline cancels it and TryCommit fails", held.IsCancelled && !held.TryCommit() && !o7.Committed);
            held.SetException(new RpcException(ErrorCodes.Timeout, "cancelled"));
            r7.Tick();
            Check("cancelled step recorded as TIMEOUT failure", (string)((JObject)o7.Result)["results"][0]["error"]["code"] == ErrorCodes.Timeout);

            // committed step keeps running past its deadline; overdue -> stepDidNotFinish
            now = TimeSpan.Zero;
            OuterCompletion o8 = new OuterCompletion { Clock = clock, Deadline = TimeSpan.FromSeconds(120) };
            IWorkCompletion held8 = null;
            FakeExecutor e8 = new FakeExecutor { Custom = (s, c) => { held8 = c; c.TryCommit(); } };
            BatchRunner r8 = new BatchRunner(Plan(false, Step("set_object", new JObject()), Step("set_object", new JObject())), o8, e8, clock);
            r8.Start();
            now = TimeSpan.FromSeconds(15.5);
            Check("committed step is never cancelled by its deadline", !held8.IsCancelled && !r8.Tick());
            now = TimeSpan.FromSeconds(17.5);
            r8.Tick();
            JObject res8 = (JObject)o8.Result;
            Check("step not finished after deadline + grace: batch aborted even with stopOnError false, says it was applied",
                res8 != null && (string)res8["stopReason"] == "stepDidNotFinish" && e8.Started.Count == 1 && ((string)res8["results"][0]["error"]["message"]).Contains("applied"));
            held8.SetResult(new JObject());
            Check("late result after abort ignored (one outer completion)", o8.Completions == 1);

            // caller gave up before any commit
            now = TimeSpan.Zero;
            OuterCompletion o9 = new OuterCompletion { Clock = clock, Deadline = TimeSpan.FromSeconds(120) };
            FakeExecutor e9 = new FakeExecutor();
            BatchRunner r9 = new BatchRunner(Plan(true, Step("get_hierarchy"), Step("set_transform", new JObject()), Step("create_object")), o9, e9, clock);
            r9.Start();
            o9.Cancelled = true;
            Check("outer cancellation before any commit cancels the running step", e9.Pending[1].IsCancelled && !e9.Pending[1].TryCommit());
            e9.Pending[1].SetException(new RpcException(ErrorCodes.Timeout, "x"));
            r9.Tick();
            Check("outer cancellation: remaining steps not started, stopReason cancelled", e9.Started.Count == 2 && (string)((JObject)o9.Result)["stopReason"] == "cancelled");

            // escape is unescaped even without references
            OuterCompletion o10 = new OuterCompletion { Clock = clock, Deadline = TimeSpan.FromSeconds(120) };
            FakeExecutor e10 = new FakeExecutor();
            new BatchRunner(Plan(true, Step("get_hierarchy", new JObject { ["name"] = "$$x" })), o10, e10, clock).Start();
            Check("'$$' unescaped for steps without references", (string)e10.ParamsSeen[0]["name"] == "$x");
        }

        // ================================================================== registry

        private sealed class SyncDispatcher : IMainThreadDispatcher
        {
            public object Invoke(Action<IWorkCompletion> start, TimeSpan timeout)
            {
                OuterCompletion c = new OuterCompletion { Clock = () => TimeSpan.Zero, Deadline = timeout };
                start(c);
                if (c.Completions == 0)
                {
                    throw new RpcException(ErrorCodes.Timeout, "not completed synchronously");
                }
                if (c.Error != null)
                {
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(c.Error).Throw();
                }
                return c.Result;
            }
        }

        private sealed class StateHost : IEditorGuardHost
        {
            public string State = "Editing";
            public int Captures;
            public EditorSnapshot Capture()
            {
                Captures++;
                return new EditorSnapshot
                {
                    EditorOpen = true, ModOpen = true, ModLoaded = true, ModName = "PB_TEST_A", FolderName = "PB_TEST_A",
                    IsEditing = State == "Editing", StateName = State,
                };
            }
            public void EnforceDefaultWorkshopId() { }
        }

        private static void RegistryBatchTests()
        {
            Console.WriteLine("-- unit: run_batch through the registry (guard per step)");
            StateHost host = new StateHost();
            bool writes = true;
            ToolRegistry reg = new ToolRegistry(new WriteSafety(() => writes, () => true, host));
            List<string> ran = new List<string>();
            reg.Register(ToolDefinition.Write("create_object", WriteGuard.OpenMod(), ctx => ctx.GetString("name"), inv =>
            {
                ran.Add("create:" + inv.Args);
                inv.Commit(TimeSpan.FromSeconds(1));
                inv.Complete(new JObject { ["guid"] = "new-" + inv.Args });
            }));
            reg.Register(ToolDefinition.Write("set_object", WriteGuard.OpenMod(), ctx => ctx.GetString("guid", true), inv =>
            {
                ran.Add("set:" + inv.Args);
                inv.Complete(new JObject { ["guid"] = (string)inv.Args });
            }));
            reg.Register(ToolDefinition.Write("delete_object", WriteGuard.OpenMod(), null, inv =>
            {
                ran.Add("delete");
                host.State = "EnteringPlayMode"; // a playtest starts meanwhile (by hand)
                inv.Complete(new JObject());
            }));
            reg.Register(ToolDefinition.Read("get_hierarchy", ctx => ctx.OnMainThread(() => new JObject { ["nodes"] = 1, ["arg"] = ctx.Params["x"] })));
            reg.Register(ToolDefinition.Read("screenshot", ctx => ctx.OnMainThreadDeferred(c => { })));
            reg.Register(ToolDefinition.Write("start_playtest", WriteGuard.OpenMod(), null, inv => { ran.Add("PLAY"); inv.Complete(null); }));
            FramePoller poller = new FramePoller(NullPluginLog.Instance);
            BatchTool.Register(reg, poller);
            SyncDispatcher d = new SyncDispatcher();

            object Call(JObject ps)
            {
                reg.TryGet("run_batch", out RegisteredTool t);
                return t.Invoke(new ToolContext("run_batch", ps, d, TimeSpan.FromSeconds(10)));
            }
            string CallCode(JObject ps) => Code(() => Call(ps));

            JObject r = (JObject)Call(Batch(Step("create_object", new JObject { ["name"] = "Floor" }, "f"), Step("set_object", new JObject { ["guid"] = "$f.guid" }),
                Step("get_hierarchy", new JObject { ["x"] = "$f.guid" })));
            Check("registry batch: write steps run behind the guard (one capture each), read step inline, references resolved",
                (int)r["completed"] == 3 && ran.SequenceEqual(new[] { "create:Floor", "set:new-Floor" }) && host.Captures == 3 // batch + 2 steps
                && (string)r["results"][2]["result"]["arg"] == "new-Floor");
            Check("run_batch registered as a write tool with guard OpenMod and 120 s", reg.TryGet("run_batch", out RegisteredTool rb) && rb.Definition.IsWrite
                && rb.Definition.Guard.Kind == WriteGuardKind.OpenMod && !rb.Definition.Guard.AllowPlayMode && rb.Definition.Timeout == TimeSpan.FromSeconds(120));

            ran.Clear();
            Check("disallowed tool refused up front: nothing runs", CallCode(Batch(Step("create_object", new JObject { ["name"] = "A" }), Step("start_playtest"))) == ErrorCodes.InvalidParams && ran.Count == 0);
            Check("screenshot refused up front", CallCode(Batch(Step("screenshot"))) == ErrorCodes.InvalidParams);
            Check("up-front parse error of a later step: nothing runs", CallCode(Batch(Step("create_object", new JObject { ["name"] = "A" }), Step("set_object"))) == ErrorCodes.InvalidParams && ran.Count == 0);

            ran.Clear();
            host.State = "Editing";
            JObject r2 = (JObject)Call(new JObject
            {
                ["steps"] = new JArray(Step("delete_object"), Step("set_object", new JObject { ["guid"] = "g" }), Step("create_object", new JObject { ["name"] = "B" })),
                ["stopOnError"] = false,
            });
            Check("each step evaluates its own guard: after a playtest started, later write steps get INVALID_STATE and do not run",
                ran.SequenceEqual(new[] { "delete" }) && (string)r2["results"][1]["error"]["code"] == ErrorCodes.InvalidState
                && (string)r2["results"][2]["error"]["code"] == ErrorCodes.InvalidState && (int)r2["completed"] == 1);
            Check("the batch itself is refused in play mode", CallCode(Batch(Step("get_hierarchy"))) == ErrorCodes.InvalidState);
            host.State = "Editing";
            writes = false;
            Check("WRITES_DISABLED refuses the batch", CallCode(Batch(Step("get_hierarchy"))) == ErrorCodes.WritesDisabled);
            writes = true;
        }

        // ================================================================== guard in play states

        private static void PlayStateGuardTests()
        {
            Console.WriteLine("-- unit: guard in play states (edit tools vs stop_playtest)");
            foreach (string st in new[] { "EnteringPlayMode", "PlayMode", "ExitingPlayMode" })
            {
                EditorSnapshot s = new EditorSnapshot
                {
                    EditorOpen = true, ModOpen = true, ModLoaded = true, ModName = "PB_TEST_A", FolderName = "PB_TEST_A", IsEditing = false, StateName = st,
                };
                WriteSafety safety = new WriteSafety(() => true, () => true, new StateHost());
                Check($"edit tool (OpenMod) refused in {st}", WriteGuardEvaluator.Evaluate(safety, WriteGuard.OpenMod(), null, s)?.Code == ErrorCodes.InvalidState);
                Check($"stop_playtest guard (OpenMod allowPlayMode) passes in {st}", WriteGuardEvaluator.Evaluate(safety, WriteGuard.OpenMod(allowPlayMode: true), null, s) == null);
                EditorSnapshot real = new EditorSnapshot
                {
                    EditorOpen = true, ModOpen = true, ModLoaded = true, ModName = "My Real Mod", FolderName = "My Real Mod", IsEditing = false, StateName = st,
                };
                Check($"stop_playtest guard still refuses a non-test mod in {st}", WriteGuardEvaluator.Evaluate(safety, WriteGuard.OpenMod(allowPlayMode: true), null, real)?.Code == ErrorCodes.GuardNotTestMod);
                Check($"stop_playtest guard still needs writes enabled in {st}",
                    WriteGuardEvaluator.Evaluate(new WriteSafety(() => false, () => true, new StateHost()), WriteGuard.OpenMod(allowPlayMode: true), null, s)?.Code == ErrorCodes.WritesDisabled);
            }
        }

        // ================================================================== playtest flow

        private sealed class FakePlayHost : IPlaytestHost
        {
            public PlayState State { get; set; } = PlayState.Editing;
            public RpcException Same;
            public int Enters, Stops;
            public Action OnEnter, OnStop;
            public bool Restored = true;
            public RpcException CheckSameMod() => Same;
            public void EnterPlayMode() { Enters++; (OnEnter ?? (() => State = PlayState.EnteringPlayMode))(); }
            public void PressStop() { Stops++; (OnStop ?? (() => State = PlayState.ExitingPlayMode))(); }
            public bool EditSceneRestored() => Restored;
        }

        private static void StartFlowTests()
        {
            Console.WriteLine("-- unit: start_playtest flow (fake editor, fake clock)");
            TimeSpan now = TimeSpan.Zero;
            Func<TimeSpan> clock = () => now;
            OuterCompletion C(double seconds) => new OuterCompletion { Clock = clock, Deadline = now + TimeSpan.FromSeconds(seconds) };

            FakePlayHost h1 = new FakePlayHost();
            OuterCompletion c1 = C(60);
            PlaytestFlow f1 = PlaytestFlow.ForStart(h1, c1, o => new JObject { ["state"] = o.FinalState.ToString() }, clock);
            Check("start: commits, presses Play once, waits in EnteringPlayMode", !f1.Start() && c1.Committed && h1.Enters == 1 && c1.Completions == 0);
            now += TimeSpan.FromSeconds(3);
            Check("start: still waiting", !f1.Tick());
            h1.State = PlayState.PlayMode;
            Check("start: completes when PlayMode is reached", f1.Tick() && (string)((JObject)c1.Result)["state"] == "PlayMode");

            now = TimeSpan.Zero;
            FakePlayHost h2 = new FakePlayHost();
            OuterCompletion c2 = C(15);
            PlaytestFlow.ForStart(h2, c2, o => null, clock).Start();
            Check("start with < 20 s left: TIMEOUT before anything, Play not pressed", (c2.Error as RpcException)?.Code == ErrorCodes.Timeout && h2.Enters == 0 && !c2.Committed
                && c2.Error.Message.Contains("nothing was changed"));

            FakePlayHost h3 = new FakePlayHost { OnEnter = () => { } }; // editor reverted to Editing (AddModInBackground failed)
            OuterCompletion c3 = C(60);
            PlaytestFlow.ForStart(h3, c3, o => null, clock).Start();
            Check("start: editor stays in Editing -> INTERNAL saying the mod was saved", (c3.Error as RpcException)?.Code == ErrorCodes.Internal && c3.Error.Message.Contains("saved"));

            now = TimeSpan.Zero;
            FakePlayHost h4 = new FakePlayHost();
            OuterCompletion c4 = C(60);
            PlaytestFlow f4 = PlaytestFlow.ForStart(h4, c4, o => null, clock);
            f4.Start();
            now = TimeSpan.FromSeconds(58.5);
            f4.Tick();
            Check("start: still waiting 1.5 s before the deadline", c4.Completions == 0);
            now = TimeSpan.FromSeconds(59.1);
            f4.Tick();
            Check("start: post-commit TIMEOUT message", (c4.Error as RpcException)?.Code == ErrorCodes.Timeout && c4.Error.Message.Contains("saved") && c4.Error.Message.Contains("stop_playtest"));

            FakePlayHost h5 = new FakePlayHost();
            OuterCompletion c5 = C(60);
            PlaytestFlow f5 = PlaytestFlow.ForStart(h5, c5, o => null, clock);
            f5.Start();
            h5.State = PlayState.ExitingPlayMode;
            f5.Tick();
            Check("start: returned to edit mode before PlayMode -> INVALID_STATE", (c5.Error as RpcException)?.Code == ErrorCodes.InvalidState);

            FakePlayHost h6 = new FakePlayHost();
            OuterCompletion c6 = C(60);
            PlaytestFlow f6 = PlaytestFlow.ForStart(h6, c6, o => null, clock);
            f6.Start();
            h6.Same = new RpcException(ErrorCodes.InvalidState, "the open mod changed");
            f6.Tick();
            Check("start: mod switched after commit -> error that says what was already done", c6.Error?.Message.Contains("but then") == true);

            FakePlayHost h7 = new FakePlayHost { OnEnter = () => throw new InvalidOperationException("disk full") };
            OuterCompletion c7 = C(60);
            PlaytestFlow.ForStart(h7, c7, o => null, clock).Start();
            Check("start: exception from Play after commit is reported with the editor state", (c7.Error as RpcException)?.Code == ErrorCodes.Internal
                && c7.Error.Message.Contains("disk full") && c7.Error.Message.Contains("state now"));

            OuterCompletion c8 = C(60);
            c8.Cancelled = true;
            FakePlayHost h8 = new FakePlayHost();
            PlaytestFlow.ForStart(h8, c8, o => null, clock).Start();
            Check("start: caller already gave up -> nothing pressed", h8.Enters == 0 && (c8.Error as RpcException)?.Code == ErrorCodes.Timeout);
        }

        private static void StopFlowTests()
        {
            Console.WriteLine("-- unit: stop_playtest flow (fake editor, fake clock)");
            TimeSpan now = TimeSpan.Zero;
            Func<TimeSpan> clock = () => now;
            OuterCompletion C(double seconds) => new OuterCompletion { Clock = clock, Deadline = now + TimeSpan.FromSeconds(seconds) };
            Func<PlaytestFlow.Outcome, object> build = o => new JObject
            {
                ["already"] = o.AlreadyStopped, ["stopping"] = o.AlreadyStopping, ["pressed"] = o.StopPressed, ["state"] = o.FinalState.ToString(),
            };

            FakePlayHost h1 = new FakePlayHost { State = PlayState.Editing };
            OuterCompletion c1 = C(30);
            Check("stop when editing: alreadyStopped, nothing pressed, not committed",
                PlaytestFlow.ForStop(h1, c1, build, clock).Start() && (bool)((JObject)c1.Result)["already"] && h1.Stops == 0 && !c1.Committed);

            FakePlayHost h2 = new FakePlayHost { State = PlayState.PlayMode };
            OuterCompletion c2 = C(30);
            PlaytestFlow f2 = PlaytestFlow.ForStop(h2, c2, build, clock);
            Check("stop in PlayMode: commits and presses Stop once", !f2.Start() && h2.Stops == 1 && c2.Committed);
            h2.State = PlayState.Editing;
            h2.Restored = false;
            Check("stop: waits until the edit scene is restored", !f2.Tick());
            h2.Restored = true;
            Check("stop: completes in Editing", f2.Tick() && (bool)((JObject)c2.Result)["pressed"] && !(bool)((JObject)c2.Result)["already"] && h2.Stops == 1);

            FakePlayHost h3 = new FakePlayHost { State = PlayState.ExitingPlayMode };
            OuterCompletion c3 = C(30);
            PlaytestFlow f3 = PlaytestFlow.ForStop(h3, c3, build, clock);
            f3.Start();
            h3.State = PlayState.Editing;
            f3.Tick();
            Check("stop while already exiting: waits, presses nothing, alreadyStopping", h3.Stops == 0 && !c3.Committed && (bool)((JObject)c3.Result)["stopping"]);

            now = TimeSpan.Zero;
            FakePlayHost h4 = new FakePlayHost { State = PlayState.EnteringPlayMode };
            OuterCompletion c4 = C(30);
            PlaytestFlow f4 = PlaytestFlow.ForStop(h4, c4, build, clock);
            Check("stop while entering: waits without pressing (Stop button disabled)", !f4.Start() && h4.Stops == 0 && !c4.Committed);
            now = TimeSpan.FromSeconds(4);
            h4.State = PlayState.PlayMode;
            f4.Tick();
            Check("stop while entering: presses Stop once PlayMode is reached", h4.Stops == 1 && c4.Committed);

            now = TimeSpan.Zero;
            FakePlayHost h5 = new FakePlayHost { State = PlayState.EnteringPlayMode };
            OuterCompletion c5 = C(30);
            PlaytestFlow f5 = PlaytestFlow.ForStop(h5, c5, build, clock);
            f5.Start();
            now = TimeSpan.FromSeconds(14);
            Check("stuck entering: still waiting at 14 s", !f5.Tick());
            now = TimeSpan.FromSeconds(15.1);
            f5.Tick();
            Check("stuck entering: INVALID_STATE after 15 s, nothing pressed", (c5.Error as RpcException)?.Code == ErrorCodes.InvalidState && h5.Stops == 0 && c5.Error.Message.Contains("nothing was changed"));

            now = TimeSpan.Zero;
            FakePlayHost h6 = new FakePlayHost { State = PlayState.PlayMode, OnStop = () => { } }; // disconnect ignored
            OuterCompletion c6 = C(30);
            PlaytestFlow f6 = PlaytestFlow.ForStop(h6, c6, build, clock);
            f6.Start();
            now = TimeSpan.FromSeconds(29.1);
            f6.Tick();
            Check("stop: editor never returns -> post-commit TIMEOUT saying Stop was sent", (c6.Error as RpcException)?.Code == ErrorCodes.Timeout && c6.Error.Message.Contains("Stop sequence was sent"));

            now = TimeSpan.Zero;
            FakePlayHost h7 = new FakePlayHost { State = PlayState.PlayMode };
            OuterCompletion c7 = C(8);
            PlaytestFlow.ForStop(h7, c7, build, clock).Start();
            Check("stop with < 10 s left: TIMEOUT before pressing", h7.Stops == 0 && (c7.Error as RpcException)?.Code == ErrorCodes.Timeout);

            FakePlayHost h8 = new FakePlayHost { State = PlayState.Closed };
            OuterCompletion c8 = C(30);
            PlaytestFlow.ForStop(h8, c8, build, clock).Start();
            Check("stop with editor closed: EDITOR_NOT_OPEN", (c8.Error as RpcException)?.Code == ErrorCodes.EditorNotOpen);
        }

        // ================================================================== options / clock / report

        private static void OptionsTests()
        {
            Console.WriteLine("-- unit: playtest options (toolbar player-count table)");
            Check("12 labels as in ToolbarWidget.m_playerCount", PlaytestOptions.Labels.Length == 12 && PlaytestOptions.Labels[2] == "4 Players" && PlaytestOptions.Labels[11] == "4v4 Teams");
            Check("players 4 -> index 2", PlaytestOptions.ParsePlayers(new JValue(4)) == 2);
            Check("players 2.0 -> index 0", PlaytestOptions.ParsePlayers(new JValue(2.0)) == 0);
            Check("players \"8 players\" (case-insensitive) -> 6", PlaytestOptions.ParsePlayers(new JValue("8 players")) == 6);
            Check("players \"2v2\" -> 7, \"2v2v2v2 Teams\" -> 9, \"4v4\" -> 11", PlaytestOptions.ParsePlayers(new JValue("2v2")) == 7
                && PlaytestOptions.ParsePlayers(new JValue("2v2v2v2 Teams")) == 9 && PlaytestOptions.ParsePlayers(new JValue("4v4")) == 11);
            Check("players \"3\" -> 1", PlaytestOptions.ParsePlayers(new JValue("3")) == 1);
            Check("players null -> keep current", PlaytestOptions.ParsePlayers(null) == null && PlaytestOptions.ParsePlayers(JValue.CreateNull()) == null);
            foreach (JToken bad in new JToken[] { new JValue(1), new JValue(9), new JValue(4.5), new JValue("5v5"), new JValue(true), new JArray() })
            {
                Check($"players {bad.ToString(Newtonsoft.Json.Formatting.None)} refused", Code(() => PlaytestOptions.ParsePlayers(bad)) == ErrorCodes.InvalidParams);
            }
            PlaytestOptions.Choice solo = PlaytestOptions.Describe(2);
            Check("4 Players: TeamMode 0, 4 test players, composition untouched", solo.TeamMode == 0 && solo.NumTestPlayers == 4 && solo.TeamComposition == null);
            Check("2v2v2 Teams: TeamMode 1, 6 players, 0,0,1,1,2,2", PlaytestOptions.Describe(8).TeamMode == 1 && PlaytestOptions.Describe(8).NumTestPlayers == 6
                && PlaytestOptions.Describe(8).TeamComposition.SequenceEqual(new[] { 0, 0, 1, 1, 2, 2 }));
            Check("3v3: 0,0,0,1,1,1; 4v4: 8 players", PlaytestOptions.Describe(10).TeamComposition.SequenceEqual(new[] { 0, 0, 0, 1, 1, 1 }) && PlaytestOptions.Describe(11).NumTestPlayers == 8);
            Check("index out of range throws", Code(() => PlaytestOptions.Describe(12)) == nameof(ArgumentOutOfRangeException));
        }

        private static void ClockTests()
        {
            Console.WriteLine("-- unit: playtest clock");
            PlaytestClock c = new PlaytestClock();
            DateTime t = new DateTime(2026, 11, 11, 12, 0, 0, DateTimeKind.Utc);
            c.Observe(PlayState.Editing, t);
            Check("no playtest yet", c.StartedAt == null && c.PlaySeconds(t) == null);
            c.MarkPluginStart();
            c.Observe(PlayState.EnteringPlayMode, t.AddSeconds(1));
            c.Observe(PlayState.PlayMode, t.AddSeconds(4));
            Check("start recorded, plugin-started, play time counts from PlayMode", c.StartedAt == t.AddSeconds(1) && c.StartedByPlugin && c.PlaySeconds(t.AddSeconds(10)) == 6);
            c.Observe(PlayState.ExitingPlayMode, t.AddSeconds(20));
            c.Observe(PlayState.Editing, t.AddSeconds(22));
            Check("end recorded; play time frozen at the end", c.EndedAt == t.AddSeconds(22) && c.PlaySeconds(t.AddSeconds(100)) == 18);
            c.Observe(PlayState.EnteringPlayMode, t.AddSeconds(30));
            Check("manual start afterwards: not plugin-started, timeline reset", !c.StartedByPlugin && c.PlayModeSince == null && c.EndedAt == null);
            c.MarkPluginStart();
            c.CancelPluginStart();
            c.Observe(PlayState.Editing, t.AddSeconds(31));
            c.Observe(PlayState.EnteringPlayMode, t.AddSeconds(40));
            Check("cancelled plugin start does not mark the next manual start", !c.StartedByPlugin);
            c.Observe(PlayState.Closed, t.AddSeconds(50));
            Check("editor closed resets", c.StartedAt == null && c.PlaySeconds(t) == null);
        }

        private static void ReportTests()
        {
            Console.WriteLine("-- unit: validator report");
            List<ValidationIssue> issues = new List<ValidationIssue>
            {
                new ValidationIssue { Severity = "Warning", Title = "Empty minigame description.", Description = "The minigame description has not been set." },
                new ValidationIssue { Severity = "Error", Title = "There are no player spawns in your map.", Description = "Every minigame map ...", Fixable = true },
                new ValidationIssue { Severity = "Information", Title = "Info", Description = "Info" },
                new ValidationIssue { Severity = "Error", Title = "Number of rounds must be greater than zero", Description = "Number of rounds must be greater than zero" },
            };
            JObject s = ValidationReport.Summary(issues);
            JArray arr = (JArray)s["issues"];
            Check("summary counts and valid=false", !(bool)s["valid"] && (int)s["errorCount"] == 2 && (int)s["warningCount"] == 1 && (int)s["infoCount"] == 1);
            Check("order errors, warnings, info (validator order within)", (string)arr[0]["message"] == "There are no player spawns in your map." && (string)arr[1]["severity"] == "error"
                && (string)arr[2]["severity"] == "warning" && (string)arr[3]["severity"] == "info");
            Check("description omitted when equal to the title; fixable flag", arr[1]["description"] == null && (bool)arr[0]["fixable"] && arr[0]["description"] != null);
            Check("error summary lists the titles", ValidationReport.ErrorSummary(issues).StartsWith("2 validator errors: There are no player spawns"));
            Check("no errors -> valid", (bool)ValidationReport.Summary(new List<ValidationIssue>())["valid"]);
        }

        private static void MiscTests()
        {
            Console.WriteLine("-- unit: inline dispatcher, error data envelope");
            Check("inline dispatcher runs synchronous work", (int)InlineDispatcher.Instance.Invoke<int>(() => 42, TimeSpan.FromSeconds(1)) == 42);
            Check("inline dispatcher refuses work that needs later frames", Code(() => InlineDispatcher.Instance.Invoke(c => { }, TimeSpan.FromSeconds(1))) == ErrorCodes.Internal);
            Check("inline dispatcher rethrows the work's error", Code(() => InlineDispatcher.Instance.Invoke<int>(() => throw RpcException.NotFound("x"), TimeSpan.FromSeconds(1))) == ErrorCodes.NotFound);

            ToolRegistry reg = new ToolRegistry();
            reg.Register(ToolDefinition.Read("fails", ctx => throw new RpcException(ErrorCodes.InvalidState, "bad", new JObject { ["validation"] = new JObject { ["errorCount"] = 2 } })));
            RpcHandler h = new RpcHandler(reg, new SyncDispatcher(), NullPluginLog.Instance, TimeSpan.FromSeconds(1));
            JObject env = JObject.Parse(h.Handle("{\"method\":\"fails\"}").Json);
            Check("RpcException error data is returned as error.data", (string)env["error"]["code"] == ErrorCodes.InvalidState && (int)env["error"]["data"]["validation"]["errorCount"] == 2);
            JObject env2 = JObject.Parse(h.Handle("{\"method\":\"nope\"}").Json);
            Check("errors without data have no data key", env2["error"]["data"] == null);
        }
    }
}
