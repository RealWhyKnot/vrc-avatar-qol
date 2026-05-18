// MeshFixPipeline.cs
//
// Discovers operations from an avatar, validates them as a single plan,
// detects shape-name collisions, then applies each operation with
// per-renderer transactional semantics. One pipeline run produces one
// MeshFixSession the caller must Dispose when its scope ends (preview
// stop / play mode exit / SDK postprocess).
//
// Discovery is delegated to MeshFixOperationDiscovery so adding new op
// types is a one-file change. Currently AutoTightenToBody expands to
// up to two ops (garment tighten + body hide) depending on its flags.

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace WhyKnot.AvatarQol.MeshFixes.Pipeline {

    internal static class MeshFixPipeline {

        internal sealed class RunOptions {
            public MeshFixMode Mode = MeshFixMode.Validate;
            public bool Verbose;
            /// <summary>
            /// If true, only operations whose owner survives this gate run.
            /// Used by upload/playmode entry points which respect the
            /// per-component processOnUpload/processInPlayMode flags.
            /// </summary>
            public System.Predicate<UnityEngine.Object> OwnerGate;
        }

        internal sealed class RunResult {
            public MeshFixSession Session { get; }
            public IList<string> Errors { get; }
            public IList<string> Warnings { get; }
            public IList<PlanRow> Plan { get; }
            public int OpsValidated;
            public int OpsApplied;
            public int OpsSkipped;
            public int MeshesCloned;
            public int ShapesProduced;
            public bool BuildShouldAbort { get; internal set; }

            public RunResult(MeshFixSession session) {
                Session = session;
                Errors = new List<string>();
                Warnings = new List<string>();
                Plan = new List<PlanRow>();
            }

            public bool Success => Errors.Count == 0;

            public string Summary {
                get {
                    if (OpsApplied == 0 && OpsSkipped == 0) return "No mesh fix operations found.";
                    return $"Applied {OpsApplied} op(s), skipped {OpsSkipped}, cloned {MeshesCloned} mesh(es), produced {ShapesProduced} blendshape(s).";
                }
            }
        }

        internal readonly struct PlanRow {
            public PlanRow(IMeshOperation op, SkinnedMeshRenderer target, string shapeName, PlanStatus status, string note) {
                Operation = op;
                Target = target;
                ShapeName = shapeName;
                Status = status;
                Note = note;
            }
            public IMeshOperation Operation { get; }
            public SkinnedMeshRenderer Target { get; }
            public string ShapeName { get; }
            public PlanStatus Status { get; }
            public string Note { get; }
        }

        internal enum PlanStatus { Ok, Warning, Conflict, Error, Skipped }

        public static RunResult Run(GameObject avatarRoot, RunOptions options) {
            options = options ?? new RunOptions();
            var session = new MeshFixSession();
            var result = new RunResult(session);

            if (avatarRoot == null) {
                result.Errors.Add("No avatar root was provided.");
                return result;
            }

            // 1. Discover.
            var ops = MeshFixOperationDiscovery.DiscoverOperations(avatarRoot)
                .Where(op => op != null && op.Owner != null)
                .Where(op => options.OwnerGate == null || options.OwnerGate(op.Owner))
                .ToList();

            if (ops.Count == 0) return result;

            var ctx = new MeshFixContext(avatarRoot, session, options.Mode, options.Verbose);

            // 2. Validate. Collect errors/warnings across ALL ops before deciding to apply.
            foreach (var op in ops) {
                if (op.Validate(ctx)) {
                    result.OpsValidated++;
                } else {
                    result.OpsSkipped++;
                    AddPlanForInvalid(result, op);
                }
            }

            // 3. Plan -- detect shape-name collisions. Two ops cannot produce
            //    the same shape on the same renderer. First-claim wins; later
            //    claimants are marked Conflict and skipped from Apply.
            var claimedShapes = new HashSet<(SkinnedMeshRenderer, string)>();
            var conflicts = new HashSet<IMeshOperation>();
            foreach (var op in ops) {
                if (!IsValidatedOp(op, result)) continue;
                foreach (var (renderer, name) in (op.ProducedShapes ?? Enumerable.Empty<(SkinnedMeshRenderer, string)>())) {
                    if (renderer == null || string.IsNullOrEmpty(name)) continue;
                    var key = (renderer, name);
                    if (!claimedShapes.Add(key)) {
                        conflicts.Add(op);
                        result.Errors.Add($"Shape name collision on {RendererPath(renderer)}: '{name}' produced by {op.DisplayName} clashes with an earlier operation. Rename one of them.");
                        result.Plan.Add(new PlanRow(op, renderer, name, PlanStatus.Conflict, "Same name on same renderer as another operation"));
                    } else {
                        result.Plan.Add(new PlanRow(op, renderer, name, PlanStatus.Ok, ""));
                    }
                }
            }

            if (options.Mode == MeshFixMode.Validate) {
                // Plan-only mode -- return without mutation.
                return result;
            }

            // 4. Apply. For each op that survived validation + collision detection.
            //    Wrap each renderer's writes in undo + try/catch; on exception the
            //    Session's per-renderer state restoration unwinds JUST that renderer
            //    when caller disposes; we add an error and continue.
            foreach (var op in ops) {
                if (!IsValidatedOp(op, result)) continue;
                if (conflicts.Contains(op)) { result.OpsSkipped++; continue; }

                try {
                    var recordedRenderersBefore = ctx.OpRecords.Count;
                    op.Apply(ctx);
                    int produced = ctx.OpRecords.Count - recordedRenderersBefore;
                    if (produced > 0) {
                        result.OpsApplied++;
                        result.ShapesProduced += produced;
                    } else {
                        result.OpsSkipped++;
                    }
                } catch (Exception ex) {
                    result.Errors.Add($"{op.DisplayName} on {OwnerName(op)}: {ex.Message}");
                    Debug.LogException(ex, op.Owner);
                    result.OpsSkipped++;
                }
            }

            result.MeshesCloned = ctx.MeshesCloned;
            foreach (var e in ctx.Errors) result.Errors.Add(e);
            foreach (var w in ctx.Warnings) result.Warnings.Add(w);

            if (options.Mode == MeshFixMode.Upload && result.Errors.Count > 0) {
                result.BuildShouldAbort = true;
            }

            return result;
        }

        private static bool IsValidatedOp(IMeshOperation op, RunResult result) {
            foreach (var row in result.Plan) {
                if (row.Operation == op && row.Status == PlanStatus.Skipped) return false;
            }
            return true;
        }

        private static void AddPlanForInvalid(RunResult result, IMeshOperation op) {
            // Only synthesize one Skipped row per op rather than per-shape so the
            // plan view does not duplicate the same red row N times.
            result.Plan.Add(new PlanRow(op, null, "", PlanStatus.Skipped, "Validation failed"));
        }

        private static string OwnerName(IMeshOperation op) {
            if (op == null || op.Owner == null) return "(unknown)";
            return op.Owner is Component c && c != null && c.gameObject != null ? c.gameObject.name : op.Owner.name;
        }

        private static string RendererPath(SkinnedMeshRenderer renderer) {
            if (renderer == null) return "(null)";
            var t = renderer.transform;
            var parts = new List<string>();
            while (t != null) { parts.Insert(0, t.name); t = t.parent; }
            return string.Join("/", parts);
        }
    }
}
