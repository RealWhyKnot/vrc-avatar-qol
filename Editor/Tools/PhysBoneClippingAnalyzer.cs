// PhysBoneClippingAnalyzer.cs
//
// Conservative PhysBone clipping risk estimate for the standalone
// PhysBone Clipping Risks window.
// This is not VRChat's runtime solver. It uses the actual PhysBone settings
// that strongly affect motion (pull/spring/stiffness/gravity/radius/stretch
// and collider presence) to estimate how far weighted vertices can plausibly
// move, then compares that envelope to nearby non-driven mesh surface.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;

#if VRC_SDK_VRCSDK3
using VRC.SDK3.Dynamics.PhysBone.Components;
#endif

namespace WhyKnot.AvatarQol.Tools {

    internal static class PhysBoneClippingAnalyzer {

        internal enum Severity {
            Medium,
            High,
        }

        internal sealed class Settings {
            public float WeightFloor = 0.03f;
            public float ClearanceMargin = 0.025f;
            public int MaxIssuesPerPhysBone = 8;
            public int NativePhysBoneCount;
            public int CustomPhysBoneCount;
            public int DrivenVertexSampleCount;
            public int SurfaceSampleCount;

            public void ResetStats() {
                NativePhysBoneCount = 0;
                CustomPhysBoneCount = 0;
                DrivenVertexSampleCount = 0;
                SurfaceSampleCount = 0;
            }
        }

        internal sealed class Issue {
            public Severity Severity;
            public Component PhysBoneComponent;
            public string PhysBoneSourceLabel;
            public Transform PhysBoneRoot;
            public Transform DrivenBone;
            public SkinnedMeshRenderer Renderer;
            public string RendererPath;
            public int VertexIndex;
            public Vector3 WorldPosition;
            public Vector3 NearestSurfacePosition;
            public SkinnedMeshRenderer NearestSurfaceRenderer;
            public string NearestSurfacePath;
            public float Weight;
            public float EstimatedMotion;
            public float Clearance;
            public bool HasEffectiveColliders;
            public string Reason;
            public float Score;
        }

        internal static bool SdkAvailable {
            get {
#if VRC_SDK_VRCSDK3
                return true;
#else
                return false;
#endif
            }
        }

        internal sealed class MotionReductionResult {
            public int SourcesChanged;
            public int IssuesCovered;
            public int UnsupportedSources;
            public string Summary;
        }

        internal static bool CanReduceMotion(Issue issue) {
#if !VRC_SDK_VRCSDK3
            return false;
#else
            if (issue == null || issue.PhysBoneComponent == null) return false;
            var component = issue.PhysBoneComponent;
            if (component is VRCPhysBone) return true;
            if (LooksLikeMarshmallowAuthoringComponent(component)) return true;
            return HasWritableFloat(component, "pull", "_pull", "m_Pull", "spring", "_spring", "momentum", "_momentum",
                "stiffness", "_stiffness", "m_Stiffness", "gravity", "_gravity", "maxStretch", "_maxStretch");
#endif
        }

        internal static MotionReductionResult ReduceMotionIssues(IList<Issue> issues, StringBuilder log = null) {
#if !VRC_SDK_VRCSDK3
            return new MotionReductionResult {
                UnsupportedSources = issues == null ? 0 : issues.Count,
                Summary = "Motion reduction needs VRChat SDK 3 PhysBone types.",
            };
#else
            var result = new MotionReductionResult();
            if (issues == null || issues.Count == 0) {
                result.Summary = "No risks selected for motion reduction.";
                return result;
            }

            var fixedComponents = new HashSet<Component>();
            var unsupportedComponents = new HashSet<Component>();
            foreach (var issue in issues.Where(i => i != null && i.PhysBoneComponent != null)) {
                var component = issue.PhysBoneComponent;
                if (fixedComponents.Contains(component) || unsupportedComponents.Contains(component)) continue;

                bool changed = ApplyMotionReduction(component, issue, log);
                if (changed) {
                    fixedComponents.Add(component);
                } else {
                    unsupportedComponents.Add(component);
                }
            }

            result.SourcesChanged = fixedComponents.Count;
            result.UnsupportedSources = unsupportedComponents.Count;
            result.IssuesCovered = issues.Count(i => i != null && i.PhysBoneComponent != null && fixedComponents.Contains(i.PhysBoneComponent));
            result.Summary = result.SourcesChanged == 0
                ? "No supported PhysBone settings could be adjusted."
                : $"Motion reduction adjusted {result.SourcesChanged} PhysBone source(s) covering {result.IssuesCovered} risk row(s).";
            return result;
#endif
        }

        internal static List<Issue> Scan(
            Animator animator,
            IList<SkinnedMeshRenderer> renderers,
            IList<SkinnedMeshRenderer> excludedRenderers,
            Settings settings,
            StringBuilder log = null) {
#if !VRC_SDK_VRCSDK3
            log?.AppendLine("  PhysBone clipping: VRChat SDK 3 not available; skipped.");
            return new List<Issue>();
#else
            settings = settings ?? new Settings();
            settings.ResetStats();
            var output = new List<Issue>();
            if (animator == null || renderers == null || renderers.Count == 0) return output;

            var physBones = DiscoverPhysBoneSources(animator, settings, log);
            if (physBones.Count == 0) {
                log?.AppendLine("  PhysBone clipping: no active PhysBone or supported generated PhysBone sources found.");
                return output;
            }

            var excluded = new HashSet<SkinnedMeshRenderer>(
                excludedRenderers == null
                    ? Enumerable.Empty<SkinnedMeshRenderer>()
                    : excludedRenderers.Where(r => r != null));

            var boneToPhysBones = new Dictionary<Transform, List<PhysBoneInfo>>();
            foreach (var info in physBones) {
                foreach (var bone in info.DrivenBones) {
                    if (bone == null) continue;
                    if (!boneToPhysBones.TryGetValue(bone, out var list)) {
                        list = new List<PhysBoneInfo>();
                        boneToPhysBones[bone] = list;
                    }
                    list.Add(info);
                }
            }

            var samples = new List<SurfaceSample>();
            var candidates = new List<Candidate>();
            foreach (var renderer in renderers) {
                ScanRenderer(renderer, excluded, settings, boneToPhysBones, samples, candidates, log);
            }
            settings.SurfaceSampleCount = samples.Count;
            settings.DrivenVertexSampleCount = candidates.Count;
            if (candidates.Count == 0 || samples.Count == 0) {
                log?.AppendLine($"  PhysBone clipping: {DescribeSourceCounts(settings)}, no weighted mesh vertices above the PhysBone floor.");
                return output;
            }

            var hash = new SpatialHash(Mathf.Max(0.025f, settings.ClearanceMargin));
            foreach (var sample in samples) hash.Add(sample);

            foreach (var candidate in candidates) {
                var info = candidate.PhysBone;
                var estimatedMotion = EstimateMotion(info, candidate.DrivenBone, candidate.Position);
                var searchRadius = Mathf.Clamp(estimatedMotion + settings.ClearanceMargin, settings.ClearanceMargin, 0.18f);
                SurfaceSample nearest = null;
                float nearestDistance = float.MaxValue;
                foreach (var sample in hash.Query(candidate.Position, searchRadius)) {
                    if (ReferenceEquals(sample.Renderer, candidate.Renderer) && sample.VertexIndex == candidate.VertexIndex) continue;
                    if (sample.Controllers.Contains(info.Index)) continue;

                    var dist = Vector3.Distance(candidate.Position, sample.Position);
                    if (dist < 0.001f) continue;
                    if (dist < nearestDistance) {
                        nearestDistance = dist;
                        nearest = sample;
                    }
                }
                if (nearest == null) continue;

                float motionFactor = info.HasEffectiveColliders ? 0.35f : 0.75f;
                float unsafeDistance = settings.ClearanceMargin + estimatedMotion * motionFactor;
                if (nearestDistance >= unsafeDistance) continue;

                var overlap = unsafeDistance - nearestDistance;
                var severe = !info.HasEffectiveColliders || nearestDistance < settings.ClearanceMargin;
                output.Add(new Issue {
                    Severity = severe ? Severity.High : Severity.Medium,
                    PhysBoneComponent = info.Component,
                    PhysBoneSourceLabel = info.SourceLabel,
                    PhysBoneRoot = info.Root,
                    DrivenBone = candidate.DrivenBone,
                    Renderer = candidate.Renderer,
                    RendererPath = candidate.RendererPath,
                    VertexIndex = candidate.VertexIndex,
                    WorldPosition = candidate.Position,
                    NearestSurfacePosition = nearest.Position,
                    NearestSurfaceRenderer = nearest.Renderer,
                    NearestSurfacePath = nearest.RendererPath,
                    Weight = candidate.Weight,
                    EstimatedMotion = estimatedMotion,
                    Clearance = nearestDistance,
                    HasEffectiveColliders = info.HasEffectiveColliders,
                    Score = overlap,
                    Reason = BuildReason(info, estimatedMotion, nearestDistance, settings.ClearanceMargin),
                });
            }

            output = output
                .OrderByDescending(i => i.Severity)
                .ThenByDescending(i => i.Score)
                .GroupBy(i => i.PhysBoneRoot != null ? (UnityEngine.Object)i.PhysBoneRoot : i.PhysBoneComponent)
                .SelectMany(g => g.Take(Mathf.Max(1, settings.MaxIssuesPerPhysBone)))
                .ToList();

            log?.AppendLine($"  PhysBone clipping: {DescribeSourceCounts(settings)}, {candidates.Count} driven vertex sample(s), {output.Count} risk(s).");
            return output;
#endif
        }

        internal static List<Issue> ScanOneMesh(
            Animator animator,
            SkinnedMeshRenderer targetRenderer,
            IList<SkinnedMeshRenderer> surfaceRenderers,
            Settings settings,
            StringBuilder log = null) {
#if !VRC_SDK_VRCSDK3
            log?.AppendLine("  PhysBone clipping: VRChat SDK 3 not available; skipped.");
            return new List<Issue>();
#else
            settings = settings ?? new Settings();
            settings.ResetStats();
            var output = new List<Issue>();
            if (animator == null || targetRenderer == null || targetRenderer.sharedMesh == null) return output;

            var physBones = DiscoverPhysBoneSources(animator, settings, log);
            if (physBones.Count == 0) {
                log?.AppendLine("  PhysBone clipping: no active PhysBone or supported generated PhysBone sources found.");
                return output;
            }

            var boneToPhysBones = new Dictionary<Transform, List<PhysBoneInfo>>();
            foreach (var info in physBones) {
                foreach (var bone in info.DrivenBones) {
                    if (bone == null) continue;
                    if (!boneToPhysBones.TryGetValue(bone, out var list)) {
                        list = new List<PhysBoneInfo>();
                        boneToPhysBones[bone] = list;
                    }
                    list.Add(info);
                }
            }

            var surfaceSet = new HashSet<SkinnedMeshRenderer>();
            surfaceSet.Add(targetRenderer);
            if (surfaceRenderers != null) {
                foreach (var renderer in surfaceRenderers) {
                    if (renderer != null) surfaceSet.Add(renderer);
                }
            }

            var samples = new List<SurfaceSample>();
            var candidates = new List<Candidate>();
            var excluded = new HashSet<SkinnedMeshRenderer>();
            foreach (var renderer in surfaceSet) {
                ScanRenderer(
                    renderer,
                    excluded,
                    settings,
                    boneToPhysBones,
                    samples,
                    candidates,
                    log,
                    collectCandidates: renderer == targetRenderer);
            }
            settings.SurfaceSampleCount = samples.Count;
            settings.DrivenVertexSampleCount = candidates.Count;
            if (candidates.Count == 0 || samples.Count == 0) {
                log?.AppendLine($"  PhysBone clipping: {DescribeSourceCounts(settings)}, no weighted vertices above the PhysBone floor on {AvatarQol.GetGameObjectPath(targetRenderer.gameObject)}.");
                return output;
            }

            var hash = new SpatialHash(Mathf.Max(0.025f, settings.ClearanceMargin));
            foreach (var sample in samples) hash.Add(sample);

            foreach (var candidate in candidates) {
                var info = candidate.PhysBone;
                var estimatedMotion = EstimateMotion(info, candidate.DrivenBone, candidate.Position);
                var searchRadius = Mathf.Clamp(estimatedMotion + settings.ClearanceMargin, settings.ClearanceMargin, 0.18f);
                SurfaceSample nearest = null;
                float nearestDistance = float.MaxValue;
                foreach (var sample in hash.Query(candidate.Position, searchRadius)) {
                    if (ReferenceEquals(sample.Renderer, candidate.Renderer) && sample.VertexIndex == candidate.VertexIndex) continue;
                    if (sample.Controllers.Contains(info.Index)) continue;

                    var dist = Vector3.Distance(candidate.Position, sample.Position);
                    if (dist < 0.001f) continue;
                    if (dist < nearestDistance) {
                        nearestDistance = dist;
                        nearest = sample;
                    }
                }
                if (nearest == null) continue;

                float motionFactor = info.HasEffectiveColliders ? 0.35f : 0.75f;
                float unsafeDistance = settings.ClearanceMargin + estimatedMotion * motionFactor;
                if (nearestDistance >= unsafeDistance) continue;

                var overlap = unsafeDistance - nearestDistance;
                var severe = !info.HasEffectiveColliders || nearestDistance < settings.ClearanceMargin;
                output.Add(new Issue {
                    Severity = severe ? Severity.High : Severity.Medium,
                    PhysBoneComponent = info.Component,
                    PhysBoneSourceLabel = info.SourceLabel,
                    PhysBoneRoot = info.Root,
                    DrivenBone = candidate.DrivenBone,
                    Renderer = candidate.Renderer,
                    RendererPath = candidate.RendererPath,
                    VertexIndex = candidate.VertexIndex,
                    WorldPosition = candidate.Position,
                    NearestSurfacePosition = nearest.Position,
                    NearestSurfaceRenderer = nearest.Renderer,
                    NearestSurfacePath = nearest.RendererPath,
                    Weight = candidate.Weight,
                    EstimatedMotion = estimatedMotion,
                    Clearance = nearestDistance,
                    HasEffectiveColliders = info.HasEffectiveColliders,
                    Score = overlap,
                    Reason = BuildReason(info, estimatedMotion, nearestDistance, settings.ClearanceMargin),
                });
            }

            output = output
                .OrderByDescending(i => i.Severity)
                .ThenByDescending(i => i.Score)
                .GroupBy(i => i.PhysBoneRoot != null ? (UnityEngine.Object)i.PhysBoneRoot : i.PhysBoneComponent)
                .SelectMany(g => g.Take(Mathf.Max(1, settings.MaxIssuesPerPhysBone)))
                .ToList();

            log?.AppendLine($"  PhysBone clipping: {DescribeSourceCounts(settings)}, {candidates.Count} driven vertex sample(s) on one mesh, {samples.Count} surface sample(s), {output.Count} risk(s).");
            return output;
#endif
        }

#if VRC_SDK_VRCSDK3
        private sealed class PhysBoneInfo {
            public int Index;
            public Component Component;
            public string SourceLabel;
            public Transform Root;
            public HashSet<Transform> DrivenBones = new HashSet<Transform>();
            public bool HasEffectiveColliders;
            public float EstimatedSwingDegrees;
            public float Radius;
            public float MaxStretch;
            public float Pull;
            public float Spring;
            public float Stiffness;
            public float Gravity;
            public float GravityFalloff;
        }

        private sealed class SurfaceSample {
            public SkinnedMeshRenderer Renderer;
            public string RendererPath;
            public int VertexIndex;
            public Vector3 Position;
            public readonly HashSet<int> Controllers = new HashSet<int>();
        }

        private sealed class Candidate {
            public PhysBoneInfo PhysBone;
            public Transform DrivenBone;
            public SkinnedMeshRenderer Renderer;
            public string RendererPath;
            public int VertexIndex;
            public Vector3 Position;
            public float Weight;
        }

        private static List<PhysBoneInfo> DiscoverPhysBoneSources(Animator animator, Settings settings, StringBuilder log) {
            var infos = new List<PhysBoneInfo>();
            if (animator == null) return infos;

            var seen = new HashSet<Component>();
            foreach (var pb in animator.GetComponentsInChildren<VRCPhysBone>(true)) {
                if (!IsUsableComponent(pb)) continue;
                seen.Add(pb);
                var info = CreateInfo(infos.Count, pb);
                if (info.Root == null || info.DrivenBones.Count == 0) continue;
                infos.Add(info);
                settings.NativePhysBoneCount++;
            }

            var customTypes = new Dictionary<string, int>();
            foreach (var component in animator.GetComponentsInChildren<Component>(true)) {
                if (!IsUsableComponent(component) || seen.Contains(component)) continue;

                int added = TryAddMarshmallowPhysBoneSources(component, infos);
                if (added > 0) {
                    seen.Add(component);
                    settings.CustomPhysBoneCount += added;
                    AddTypeCount(customTypes, component.GetType(), added);
                    continue;
                }

                if (!LooksLikeReflectedPhysBoneSource(component)) continue;
                var reflected = CreateInfoFromReflectedComponent(infos.Count, component, "Custom PhysBone");
                if (reflected == null || reflected.Root == null || reflected.DrivenBones.Count == 0) continue;
                infos.Add(reflected);
                seen.Add(component);
                settings.CustomPhysBoneCount++;
                AddTypeCount(customTypes, component.GetType(), 1);
            }

            log?.AppendLine($"  PhysBone clipping sources: {DescribeSourceCounts(settings)}.");
            foreach (var pair in customTypes.OrderBy(p => p.Key)) {
                log?.AppendLine($"    custom/generated: {pair.Value} from {pair.Key}");
            }
            return infos;
        }

        private static bool IsUsableComponent(Component component) {
            if (component == null) return false;
            if (component is Behaviour behaviour && !behaviour.enabled) return false;
            return true;
        }

        private static void AddTypeCount(Dictionary<string, int> counts, Type type, int amount) {
            var name = type != null ? type.FullName ?? type.Name : "Unknown";
            counts[name] = counts.TryGetValue(name, out var existing) ? existing + amount : amount;
        }

        private static string DescribeSourceCounts(Settings settings) {
            int total = settings.NativePhysBoneCount + settings.CustomPhysBoneCount;
            if (settings.CustomPhysBoneCount == 0) return $"{total} PhysBone source(s)";
            return $"{total} PhysBone source(s): {settings.NativePhysBoneCount} live, {settings.CustomPhysBoneCount} generated/custom";
        }

        private static PhysBoneInfo CreateInfo(int index, VRCPhysBone pb) {
            var root = pb.rootTransform != null ? pb.rootTransform : pb.transform;
            bool collisionDisabled = pb.allowCollision.ToString() == "False";
            bool hasColliders = !collisionDisabled && pb.colliders != null && pb.colliders.Any(c => c != null);
            return new PhysBoneInfo {
                Index = index,
                Component = pb,
                SourceLabel = "VRCPhysBone",
                Root = root,
                DrivenBones = BuildDrivenBoneSet(root, TransformEnumerableToSet(pb.ignoreTransforms)),
                HasEffectiveColliders = hasColliders,
                EstimatedSwingDegrees = EstimateSwingDegrees(
                    pb.pull,
                    pb.stiffness,
                    pb.spring,
                    pb.gravity,
                    pb.gravityFalloff,
                    pb.allowGrabbing.ToString() != "False",
                    pb.allowPosing.ToString() != "False",
                    TryGetReflectedLimitAngle(pb)),
                Radius = Mathf.Max(0f, pb.radius),
                MaxStretch = Mathf.Max(0f, pb.maxStretch),
                Pull = Mathf.Clamp01(pb.pull),
                Spring = Mathf.Clamp01(pb.spring),
                Stiffness = Mathf.Clamp01(pb.stiffness),
                Gravity = pb.gravity,
                GravityFalloff = Mathf.Clamp01(pb.gravityFalloff),
            };
        }

        private static int TryAddMarshmallowPhysBoneSources(Component component, List<PhysBoneInfo> infos) {
            if (component == null || !LooksLikeMarshmallowAuthoringComponent(component)) return 0;

            var roots = new List<Transform>();
            var left = ReadTransform(component, "_Breast_L", "Breast_L", "breast_L");
            var right = ReadTransform(component, "_Breast_R", "Breast_R", "breast_R");
            if (left != null) roots.Add(left);
            if (right != null && right != left) roots.Add(right);
            if (roots.Count == 0) return 0;

            float pull = ReadFloat(component, 0.1f, "_PhysBone_Pull", "PhysBone_Pull", "pull");
            float spring = ReadFloat(component, 0.5f, "_PhysBone_Momentum", "PhysBone_Momentum", "spring");
            float stiffness = ReadFloat(component, 0.25f, "_PhysBone_Stiffness", "PhysBone_Stiffness", "stiffness");
            float gravity = ReadFloat(component, 0.02f, "_PhysBone_Gravity", "PhysBone_Gravity", "gravity");
            float gravityFalloff = ReadFloat(component, 1f, "_PhysBone_GravityFalloff", "PhysBone_GravityFalloff", "gravityFalloff");
            float radius = ReadFloat(component, 0.06f, "_PhysBone_Collision_Radius", "PhysBone_Collision_Radius", "radius");
            float maxStretch = ReadFloat(component, 0.3f, "_PhysBone_Max_Stretch", "PhysBone_Max_Stretch", "maxStretch");
            float limitAngle = ReadFloat(component, 35f, "_PhysBone_Limit_Angle", "PhysBone_Limit_Angle", "maxAngleX", "limitAngle");
            bool allowCollision = ReadAdvancedBool(component, true, "_PhysBone_AllowCollision", "PhysBone_AllowCollision", "allowCollision");
            bool allowGrabbing = ReadAdvancedBool(component, true, "_PhysBone_AllowGrabbing", "PhysBone_AllowGrabbing", "allowGrabbing");
            bool allowPosing = ReadAdvancedBool(component, false, "_PhysBone_AllowPosing", "PhysBone_AllowPosing", "allowPosing");

            bool noSquish = ReadBool(component, false, "_nosquish", "nosquish");
            bool onlySquish = ReadBool(component, false, "_onlysquish", "onlysquish");
            if (noSquish || onlySquish) maxStretch = 0f;
            if (onlySquish) {
                pull = Mathf.Max(pull, 0.85f);
                stiffness = Mathf.Max(stiffness, 0.75f);
            }

            int added = 0;
            foreach (var root in roots) {
                var driven = BuildDrivenBoneSet(root, null);
                if (driven.Count == 0) continue;
                infos.Add(new PhysBoneInfo {
                    Index = infos.Count,
                    Component = component,
                    SourceLabel = "Marshmallow PB generated PhysBone",
                    Root = root,
                    DrivenBones = driven,
                    HasEffectiveColliders = allowCollision,
                    EstimatedSwingDegrees = EstimateSwingDegrees(
                        pull,
                        stiffness,
                        spring,
                        gravity,
                        gravityFalloff,
                        allowGrabbing,
                        allowPosing,
                        limitAngle),
                    Radius = Mathf.Max(0f, radius),
                    MaxStretch = Mathf.Max(0f, maxStretch),
                    Pull = Mathf.Clamp01(pull),
                    Spring = Mathf.Clamp01(spring),
                    Stiffness = Mathf.Clamp01(stiffness),
                    Gravity = gravity,
                    GravityFalloff = Mathf.Clamp01(gravityFalloff),
                });
                added++;
            }
            return added;
        }

        private static bool LooksLikeMarshmallowAuthoringComponent(Component component) {
            var typeText = GetTypeText(component.GetType());
            if (typeText.IndexOf("marshmallow", StringComparison.OrdinalIgnoreCase) < 0 &&
                typeText.IndexOf("cake_PB", StringComparison.OrdinalIgnoreCase) < 0) {
                return false;
            }
            return HasMember(component, "_Breast_L", "_Breast_R") &&
                   HasMember(component, "_PhysBone_Pull", "_PhysBone_Collision_Radius", "_PhysBone_Max_Stretch");
        }

        private static bool LooksLikeReflectedPhysBoneSource(Component component) {
            if (component == null) return false;
            var typeText = GetTypeText(component.GetType());
            if (typeText.IndexOf("Collider", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            if (typeText.IndexOf("PhysBone", StringComparison.OrdinalIgnoreCase) < 0 &&
                typeText.IndexOf("DynamicBone", StringComparison.OrdinalIgnoreCase) < 0) {
                return false;
            }
            return HasMember(component, "rootTransform", "root", "physBoneRoot", "targetTransform") &&
                   HasMember(component, "pull", "spring", "stiffness", "gravity", "radius", "maxStretch");
        }

        private static PhysBoneInfo CreateInfoFromReflectedComponent(int index, Component component, string sourceLabel) {
            var root = ReadTransform(component, "rootTransform", "RootTransform", "root", "Root", "_rootTransform", "_root", "physBoneRoot", "targetTransform");
            if (root == null) return null;

            var ignored = ReadTransformSet(component, "ignoreTransforms", "_ignoreTransforms", "ignoredTransforms", "exclusions");
            var driven = BuildDrivenBoneSet(root, ignored);
            if (driven.Count == 0) return null;

            float pull = ReadFloat(component, 0f, "pull", "_pull", "m_Pull");
            float spring = ReadFloat(component, 0f, "spring", "_spring", "momentum", "_momentum", "m_Elasticity");
            float stiffness = ReadFloat(component, 0f, "stiffness", "_stiffness", "m_Stiffness");
            float gravity = ReadFloat(component, 0f, "gravity", "_gravity", "m_Gravity");
            float gravityFalloff = ReadFloat(component, 0f, "gravityFalloff", "_gravityFalloff");
            float radius = ReadFloat(component, 0f, "radius", "_radius", "m_Radius");
            float maxStretch = ReadFloat(component, 0f, "maxStretch", "_maxStretch", "stretch", "max_stretch");
            bool allowCollision = ReadAdvancedBool(component, true, "allowCollision", "_allowCollision");
            bool allowGrabbing = ReadAdvancedBool(component, true, "allowGrabbing", "_allowGrabbing");
            bool allowPosing = ReadAdvancedBool(component, false, "allowPosing", "_allowPosing");
            bool hasColliders = allowCollision && CountObjectReferences(GetMemberValue(component, "colliders", "_colliders", "collisionColliders")) > 0;

            return new PhysBoneInfo {
                Index = index,
                Component = component,
                SourceLabel = sourceLabel,
                Root = root,
                DrivenBones = driven,
                HasEffectiveColliders = hasColliders,
                EstimatedSwingDegrees = EstimateSwingDegrees(
                    pull,
                    stiffness,
                    spring,
                    gravity,
                    gravityFalloff,
                    allowGrabbing,
                    allowPosing,
                    TryGetReflectedLimitAngle(component)),
                Radius = Mathf.Max(0f, radius),
                MaxStretch = Mathf.Max(0f, maxStretch),
                Pull = Mathf.Clamp01(pull),
                Spring = Mathf.Clamp01(spring),
                Stiffness = Mathf.Clamp01(stiffness),
                Gravity = gravity,
                GravityFalloff = Mathf.Clamp01(gravityFalloff),
            };
        }

        private static bool ApplyMotionReduction(Component component, Issue issue, StringBuilder log) {
            if (component == null) return false;
            if (component is VRCPhysBone pb) {
                return ApplyLivePhysBoneMotionReduction(pb, issue, log);
            }

            if (LooksLikeMarshmallowAuthoringComponent(component)) {
                return ApplyMarshmallowMotionReduction(component, issue, log);
            }

            return ApplyReflectedMotionReduction(component, issue, log);
        }

        private static bool ApplyLivePhysBoneMotionReduction(VRCPhysBone pb, Issue issue, StringBuilder log) {
            Undo.RecordObject(pb, "Avatar QoL PhysBone clipping motion reduction");
            bool high = issue != null && issue.Severity == Severity.High;
            bool changed = false;

            var nextPull = Mathf.Clamp01(Mathf.Max(pb.pull + (high ? 0.25f : 0.15f), high ? 0.60f : 0.45f));
            if (!Mathf.Approximately(pb.pull, nextPull)) {
                pb.pull = nextPull;
                changed = true;
            }

            var nextStiffness = Mathf.Clamp01(Mathf.Max(pb.stiffness + (high ? 0.25f : 0.15f), high ? 0.55f : 0.40f));
            if (!Mathf.Approximately(pb.stiffness, nextStiffness)) {
                pb.stiffness = nextStiffness;
                changed = true;
            }

            var nextSpring = Mathf.Clamp01(pb.spring * (high ? 0.45f : 0.65f));
            if (!Mathf.Approximately(pb.spring, nextSpring)) {
                pb.spring = nextSpring;
                changed = true;
            }

            var nextGravity = Mathf.MoveTowards(pb.gravity, 0f, high ? 0.30f : 0.15f);
            if (!Mathf.Approximately(pb.gravity, nextGravity)) {
                pb.gravity = nextGravity;
                changed = true;
            }

            var nextMaxStretch = Mathf.Max(0f, pb.maxStretch * (high ? 0.40f : 0.60f));
            if (!Mathf.Approximately(pb.maxStretch, nextMaxStretch)) {
                pb.maxStretch = nextMaxStretch;
                changed = true;
            }
            if (issue != null && !issue.HasEffectiveColliders) {
                changed |= TrySetAdvancedBoolTrue(pb, "allowCollision");
            }

            if (changed) {
                EditorUtility.SetDirty(pb);
                log?.AppendLine($"  Motion reduction: tightened live PhysBone on {AvatarQol.GetGameObjectPath(pb.gameObject)}.");
            }
            return changed;
        }

        private static bool ApplyMarshmallowMotionReduction(Component component, Issue issue, StringBuilder log) {
            Undo.RecordObject(component, "Avatar QoL PhysBone clipping motion reduction");
            bool high = issue != null && issue.Severity == Severity.High;
            bool changed = false;

            changed |= TrySetFloat(component, v => Mathf.Clamp01(Mathf.Max(v + (high ? 0.25f : 0.15f), high ? 0.60f : 0.45f)),
                "_PhysBone_Pull", "PhysBone_Pull");
            changed |= TrySetFloat(component, v => Mathf.Clamp01(Mathf.Max(v + (high ? 0.25f : 0.15f), high ? 0.55f : 0.40f)),
                "_PhysBone_Stiffness", "PhysBone_Stiffness");
            changed |= TrySetFloat(component, v => Mathf.Clamp01(v * (high ? 0.45f : 0.65f)),
                "_PhysBone_Momentum", "PhysBone_Momentum");
            changed |= TrySetFloat(component, v => Mathf.MoveTowards(v, 0f, high ? 0.30f : 0.15f),
                "_PhysBone_Gravity", "PhysBone_Gravity");
            changed |= TrySetFloat(component, v => Mathf.Max(0f, v * (high ? 0.40f : 0.60f)),
                "_PhysBone_Max_Stretch", "PhysBone_Max_Stretch");
            changed |= TrySetBool(component, true, "_breastInterference_BreakPreventionCollider", "breastInterference_BreakPreventionCollider");
            changed |= TrySetAdvancedBoolTrue(component, "_PhysBone_AllowCollision", "PhysBone_AllowCollision");

            if (changed) {
                EditorUtility.SetDirty(component);
                log?.AppendLine($"  Motion reduction: tightened Marshmallow PB settings on {AvatarQol.GetGameObjectPath(component.gameObject)}.");
            }
            return changed;
        }

        private static bool ApplyReflectedMotionReduction(Component component, Issue issue, StringBuilder log) {
            Undo.RecordObject(component, "Avatar QoL PhysBone clipping motion reduction");
            bool high = issue != null && issue.Severity == Severity.High;
            bool changed = false;

            changed |= TrySetFloat(component, v => Mathf.Clamp01(Mathf.Max(v + (high ? 0.25f : 0.15f), high ? 0.60f : 0.45f)),
                "pull", "_pull", "m_Pull");
            changed |= TrySetFloat(component, v => Mathf.Clamp01(Mathf.Max(v + (high ? 0.25f : 0.15f), high ? 0.55f : 0.40f)),
                "stiffness", "_stiffness", "m_Stiffness");
            changed |= TrySetFloat(component, v => Mathf.Clamp01(v * (high ? 0.45f : 0.65f)),
                "spring", "_spring", "momentum", "_momentum", "m_Elasticity");
            changed |= TrySetFloat(component, v => Mathf.MoveTowards(v, 0f, high ? 0.30f : 0.15f),
                "gravity", "_gravity", "m_Gravity");
            changed |= TrySetFloat(component, v => Mathf.Max(0f, v * (high ? 0.40f : 0.60f)),
                "maxStretch", "_maxStretch", "stretch", "max_stretch");
            if (issue != null && !issue.HasEffectiveColliders) {
                changed |= TrySetAdvancedBoolTrue(component, "allowCollision", "_allowCollision");
            }

            if (changed) {
                EditorUtility.SetDirty(component);
                log?.AppendLine($"  Motion reduction: tightened reflected PhysBone settings on {AvatarQol.GetGameObjectPath(component.gameObject)}.");
            }
            return changed;
        }

        private static HashSet<Transform> BuildDrivenBoneSet(Transform root, HashSet<Transform> ignored) {
            var driven = new HashSet<Transform>();
            if (root == null) return driven;
            ignored = ignored ?? new HashSet<Transform>();
            foreach (var t in root.GetComponentsInChildren<Transform>(true)) {
                if (t == null) continue;
                if (IsIgnoredByPhysBone(t, root, ignored)) continue;
                driven.Add(t);
            }
            return driven;
        }

        private static bool IsIgnoredByPhysBone(Transform t, Transform root, HashSet<Transform> ignored) {
            if (ignored.Count == 0) return false;
            var cur = t;
            while (cur != null) {
                if (ignored.Contains(cur)) return true;
                if (cur == root) break;
                cur = cur.parent;
            }
            return false;
        }

        private static void ScanRenderer(
            SkinnedMeshRenderer renderer,
            HashSet<SkinnedMeshRenderer> excluded,
            Settings settings,
            Dictionary<Transform, List<PhysBoneInfo>> boneToPhysBones,
            List<SurfaceSample> samples,
            List<Candidate> candidates,
            StringBuilder log,
            bool collectCandidates = true) {
            if (renderer == null || renderer.sharedMesh == null) return;
            if (excluded.Contains(renderer)) return;
            var mesh = renderer.sharedMesh;
            if (!mesh.isReadable) return;
            var bones = renderer.bones;
            if (bones == null || bones.Length == 0) return;

            var verts = mesh.vertices;
            var weights = mesh.GetAllBoneWeights();
            var bonesPerVertex = mesh.GetBonesPerVertex();
            var bindposes = mesh.bindposes;
            if (verts.Length != mesh.vertexCount || bonesPerVertex.Length != mesh.vertexCount) return;

            int cursor = 0;
            var path = AvatarQol.GetGameObjectPath(renderer.gameObject);
            for (int v = 0; v < mesh.vertexCount; v++) {
                int weightCount = bonesPerVertex[v];
                var world = ComputeWorldPosition(verts[v], cursor, weightCount);
                var sample = new SurfaceSample {
                    Renderer = renderer,
                    RendererPath = path,
                    VertexIndex = v,
                    Position = world,
                };

                var bestByPhysBone = collectCandidates ? new Dictionary<int, Candidate>() : null;
                for (int w = 0; w < weightCount; w++) {
                    var bw = weights[cursor + w];
                    if (bw.boneIndex < 0 || bw.boneIndex >= bones.Length) continue;
                    if (bw.weight < settings.WeightFloor) continue;
                    var bone = bones[bw.boneIndex];
                    if (bone == null) continue;
                    if (!boneToPhysBones.TryGetValue(bone, out var owners)) continue;
                    foreach (var info in owners) {
                        sample.Controllers.Add(info.Index);
                        if (!collectCandidates) continue;
                        if (!bestByPhysBone.TryGetValue(info.Index, out var existing) || bw.weight > existing.Weight) {
                            bestByPhysBone[info.Index] = new Candidate {
                                PhysBone = info,
                                DrivenBone = bone,
                                Renderer = renderer,
                                RendererPath = path,
                                VertexIndex = v,
                                Position = world,
                                Weight = bw.weight,
                            };
                        }
                    }
                }

                samples.Add(sample);
                if (collectCandidates) candidates.AddRange(bestByPhysBone.Values);
                cursor += weightCount;
            }

            Vector3 ComputeWorldPosition(Vector3 meshLocal, int weightCursor, int weightCountForVertex) {
                int primaryIdx = -1;
                float primaryWeight = 0f;
                for (int w = 0; w < weightCountForVertex; w++) {
                    var bw = weights[weightCursor + w];
                    if (bw.boneIndex < 0 || bw.boneIndex >= bones.Length) continue;
                    if (bones[bw.boneIndex] == null) continue;
                    if (bw.weight > primaryWeight) {
                        primaryWeight = bw.weight;
                        primaryIdx = bw.boneIndex;
                    }
                }

                if (primaryIdx >= 0 && bindposes != null && primaryIdx < bindposes.Length) {
                    var boneLocal = bindposes[primaryIdx].MultiplyPoint3x4(meshLocal);
                    return bones[primaryIdx].TransformPoint(boneLocal);
                }
                return renderer.transform.TransformPoint(meshLocal);
            }
        }

        private static float EstimateMotion(PhysBoneInfo info, Transform drivenBone, Vector3 vertexWorld) {
            if (info.Root == null || drivenBone == null) return 0f;
            float lever = Mathf.Max(
                Mathf.Max(Vector3.Distance(info.Root.position, drivenBone.position),
                    Vector3.Distance(info.Root.position, vertexWorld)),
                0.01f);
            float radians = info.EstimatedSwingDegrees * Mathf.Deg2Rad;
            float swing = 2f * lever * Mathf.Sin(radians * 0.5f);
            float stretch = lever * info.MaxStretch;
            return Mathf.Clamp(swing + stretch + info.Radius, 0f, Mathf.Max(0.05f, lever * 1.5f + info.Radius));
        }

        private static float EstimateSwingDegrees(
            float rawPull,
            float rawStiffness,
            float rawSpring,
            float rawGravity,
            float rawGravityFalloff,
            bool allowGrabbing,
            bool allowPosing,
            float reflectedLimit) {
            float pull = Mathf.Clamp01(rawPull);
            float stiffness = Mathf.Clamp01(rawStiffness);
            float spring = Mathf.Clamp01(rawSpring);
            float gravity = Mathf.Abs(rawGravity) * (1f - Mathf.Clamp01(rawGravityFalloff) * 0.5f);

            float angle = 72f;
            angle -= pull * 28f;
            angle -= stiffness * 30f;
            angle += spring * 18f;
            angle += gravity * 30f;
            if (allowGrabbing || allowPosing) {
                angle += 15f;
            }

            if (reflectedLimit > 0.01f) angle = Mathf.Min(angle, reflectedLimit);
            return Mathf.Clamp(angle, 8f, 110f);
        }

        private static float TryGetReflectedLimitAngle(object source) {
            var limitType = GetMemberValue(source, "limitType")?.ToString();
            if (!string.IsNullOrEmpty(limitType) && limitType.IndexOf("None", StringComparison.OrdinalIgnoreCase) >= 0) {
                return -1f;
            }

            float best = -1f;
            foreach (var name in new[] { "maxAngleX", "maxAngleZ", "maxAngle", "limitAngle", "_PhysBone_Limit_Angle", "_inertia_LimitAngle" }) {
                var value = ReadFloat(source, -1f, name);
                if (value > best) best = value;
            }
            return best;
        }

        private static string BuildReason(PhysBoneInfo info, float estimatedMotion, float clearance, float margin) {
            var colliderText = info.HasEffectiveColliders
                ? "colliders are assigned, but the clearance is still small"
                : "no effective PhysBone colliders are assigned";
            var sourcePrefix = info.SourceLabel == "VRCPhysBone" || string.IsNullOrEmpty(info.SourceLabel)
                ? ""
                : $"{info.SourceLabel}: ";
            return $"{sourcePrefix}{ToCm(estimatedMotion)} cm estimated motion envelope vs {ToCm(clearance)} cm nearest mesh clearance ({colliderText}; margin {ToCm(margin)} cm).";
        }

        private static bool HasMember(object source, params string[] names) {
            if (source == null) return false;
            var type = source.GetType();
            foreach (var name in names) {
                if (FindField(type, name) != null || FindProperty(type, name) != null) return true;
            }
            return false;
        }

        private static object GetMemberValue(object source, params string[] names) {
            if (source == null) return null;
            var type = source.GetType();
            foreach (var name in names) {
                var field = FindField(type, name);
                if (field != null) {
                    try {
                        return field.GetValue(source);
                    } catch {
                        return null;
                    }
                }

                var prop = FindProperty(type, name);
                if (prop != null) {
                    try {
                        return prop.GetValue(source, null);
                    } catch {
                        return null;
                    }
                }
            }
            return null;
        }

        private static FieldInfo FindField(Type type, string name) {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;
            for (var t = type; t != null; t = t.BaseType) {
                foreach (var field in t.GetFields(flags)) {
                    if (string.Equals(field.Name, name, StringComparison.OrdinalIgnoreCase)) return field;
                }
            }
            return null;
        }

        private static PropertyInfo FindProperty(Type type, string name) {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;
            for (var t = type; t != null; t = t.BaseType) {
                foreach (var prop in t.GetProperties(flags)) {
                    if (prop.GetIndexParameters().Length != 0) continue;
                    if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase)) return prop;
                }
            }
            return null;
        }

        private static Transform ReadTransform(object source, params string[] names) {
            return ObjectToTransform(GetMemberValue(source, names));
        }

        private static HashSet<Transform> ReadTransformSet(object source, params string[] names) {
            return TransformEnumerableToSet(GetMemberValue(source, names));
        }

        private static HashSet<Transform> TransformEnumerableToSet(object value) {
            var output = new HashSet<Transform>();
            if (value == null) return output;

            var single = ObjectToTransform(value);
            if (single != null) {
                output.Add(single);
                return output;
            }

            if (value is string) return output;
            if (value is IEnumerable enumerable) {
                foreach (var item in enumerable) {
                    var t = ObjectToTransform(item);
                    if (t != null) output.Add(t);
                }
            }
            return output;
        }

        private static Transform ObjectToTransform(object value) {
            if (value == null) return null;
            if (value is Transform transform) return transform;
            if (value is GameObject go) return go.transform;
            if (value is Component component) return component.transform;
            return null;
        }

        private static float ReadFloat(object source, float fallback, params string[] names) {
            var value = GetMemberValue(source, names);
            if (value == null) return fallback;
            try {
                if (value is Vector3 vector) return vector.magnitude;
                return Convert.ToSingle(value);
            } catch {
                return fallback;
            }
        }

        private static bool HasWritableFloat(object source, params string[] names) {
            if (source == null) return false;
            var type = source.GetType();
            foreach (var name in names) {
                var field = FindField(type, name);
                if (field != null && !field.IsInitOnly && CanStoreFloat(field.FieldType)) return true;
                var prop = FindProperty(type, name);
                if (prop != null && prop.CanWrite && CanStoreFloat(prop.PropertyType)) return true;
            }
            return false;
        }

        private static bool TrySetFloat(object source, Func<float, float> adjust, params string[] names) {
            if (source == null || adjust == null) return false;
            var type = source.GetType();
            foreach (var name in names) {
                var field = FindField(type, name);
                if (field != null && !field.IsInitOnly && TryReadFloatValue(field.GetValue(source), out var fieldValue)) {
                    var next = adjust(fieldValue);
                    if (Mathf.Approximately(fieldValue, next)) return false;
                    try {
                        field.SetValue(source, ConvertFloatForType(next, field.FieldType));
                        return true;
                    } catch {
                        return false;
                    }
                }

                var prop = FindProperty(type, name);
                if (prop != null && prop.CanWrite && TryReadFloatValue(prop.GetValue(source, null), out var propValue)) {
                    var next = adjust(propValue);
                    if (Mathf.Approximately(propValue, next)) return false;
                    try {
                        prop.SetValue(source, ConvertFloatForType(next, prop.PropertyType), null);
                        return true;
                    } catch {
                        return false;
                    }
                }
            }
            return false;
        }

        private static bool TrySetBool(object source, bool next, params string[] names) {
            if (source == null) return false;
            var type = source.GetType();
            foreach (var name in names) {
                var field = FindField(type, name);
                if (field != null && !field.IsInitOnly && TrySetBoolMember(source, field, next)) return true;
                var prop = FindProperty(type, name);
                if (prop != null && prop.CanWrite && TrySetBoolMember(source, prop, next)) return true;
            }
            return false;
        }

        private static bool TrySetAdvancedBoolTrue(object source, params string[] names) {
            if (source == null) return false;
            var type = source.GetType();
            foreach (var name in names) {
                var field = FindField(type, name);
                if (field != null && !field.IsInitOnly && TrySetAdvancedBoolMember(source, field)) return true;
                var prop = FindProperty(type, name);
                if (prop != null && prop.CanWrite && TrySetAdvancedBoolMember(source, prop)) return true;
            }
            return false;
        }

        private static bool ReadBool(object source, bool fallback, params string[] names) {
            var value = GetMemberValue(source, names);
            if (value == null) return fallback;
            try {
                if (value is bool b) return b;
                if (value is int i) return i != 0;
                if (value is float f) return !Mathf.Approximately(f, 0f);
                var text = value.ToString();
                if (string.Equals(text, "True", StringComparison.OrdinalIgnoreCase)) return true;
                if (string.Equals(text, "False", StringComparison.OrdinalIgnoreCase)) return false;
            } catch {
                return fallback;
            }
            return fallback;
        }

        private static bool ReadAdvancedBool(object source, bool fallback, params string[] names) {
            var value = GetMemberValue(source, names);
            if (value == null) return fallback;
            try {
                if (value is bool b) return b;
                if (value is int i) return i != 0;
                if (value is float f) return !Mathf.Approximately(f, 0f);
                var text = value.ToString();
                if (text.IndexOf("False", StringComparison.OrdinalIgnoreCase) >= 0) return false;
                if (text.IndexOf("True", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                if (text.IndexOf("Self", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                if (text.IndexOf("Other", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            } catch {
                return fallback;
            }
            return fallback;
        }

        private static bool CanStoreFloat(Type type) {
            return type == typeof(float) || type == typeof(double) || type == typeof(int);
        }

        private static bool TryReadFloatValue(object value, out float output) {
            output = 0f;
            if (value == null) return false;
            try {
                output = Convert.ToSingle(value);
                return true;
            } catch {
                return false;
            }
        }

        private static object ConvertFloatForType(float value, Type type) {
            if (type == typeof(double)) return (double)value;
            if (type == typeof(int)) return Mathf.RoundToInt(value);
            return value;
        }

        private static bool TrySetBoolMember(object source, FieldInfo field, bool next) {
            var current = field.GetValue(source);
            var converted = ConvertBoolForType(next, field.FieldType);
            if (converted == null || ValuesEqual(current, converted)) return false;
            try {
                field.SetValue(source, converted);
                return true;
            } catch {
                return false;
            }
        }

        private static bool TrySetBoolMember(object source, PropertyInfo prop, bool next) {
            var current = prop.GetValue(source, null);
            var converted = ConvertBoolForType(next, prop.PropertyType);
            if (converted == null || ValuesEqual(current, converted)) return false;
            try {
                prop.SetValue(source, converted, null);
                return true;
            } catch {
                return false;
            }
        }

        private static bool TrySetAdvancedBoolMember(object source, FieldInfo field) {
            var current = field.GetValue(source);
            var converted = ConvertAdvancedBoolTrue(field.FieldType);
            if (converted == null || ValuesEqual(current, converted)) return false;
            try {
                field.SetValue(source, converted);
                return true;
            } catch {
                return false;
            }
        }

        private static bool TrySetAdvancedBoolMember(object source, PropertyInfo prop) {
            var current = prop.GetValue(source, null);
            var converted = ConvertAdvancedBoolTrue(prop.PropertyType);
            if (converted == null || ValuesEqual(current, converted)) return false;
            try {
                prop.SetValue(source, converted, null);
                return true;
            } catch {
                return false;
            }
        }

        private static object ConvertBoolForType(bool value, Type type) {
            if (type == typeof(bool)) return value;
            if (type == typeof(int)) return value ? 1 : 0;
            if (type == typeof(float)) return value ? 1f : 0f;
            if (type == typeof(double)) return value ? 1d : 0d;
            return null;
        }

        private static object ConvertAdvancedBoolTrue(Type type) {
            if (type == typeof(bool)) return true;
            if (type == typeof(int)) return 1;
            if (type == typeof(float)) return 1f;
            if (type == typeof(double)) return 1d;
            if (!type.IsEnum) return null;
            try {
                return Enum.Parse(type, "True", true);
            } catch {
                try {
                    return Enum.ToObject(type, 1);
                } catch {
                    return null;
                }
            }
        }

        private static bool ValuesEqual(object left, object right) {
            if (left == null || right == null) return left == right;
            if (TryReadFloatValue(left, out var leftFloat) && TryReadFloatValue(right, out var rightFloat)) {
                return Mathf.Approximately(leftFloat, rightFloat);
            }
            return left.Equals(right);
        }

        private static int CountObjectReferences(object value) {
            if (value == null) return 0;
            if (value is string) return 0;
            if (value is UnityEngine.Object unityObject) return unityObject != null ? 1 : 0;
            if (value is IEnumerable enumerable) {
                int count = 0;
                foreach (var item in enumerable) {
                    if (item == null) continue;
                    if (item is UnityEngine.Object itemObject && itemObject == null) continue;
                    count++;
                }
                return count;
            }
            return 1;
        }

        private static string GetTypeText(Type type) {
            var sb = new StringBuilder();
            for (var t = type; t != null; t = t.BaseType) {
                if (sb.Length > 0) sb.Append(" ");
                sb.Append(t.FullName ?? t.Name);
            }
            return sb.ToString();
        }

        private static string ToCm(float metres) {
            return (metres * 100f).ToString("0.0");
        }

        private sealed class SpatialHash {
            private readonly float _cellSize;
            private readonly Dictionary<Vector3Int, List<SurfaceSample>> _cells = new Dictionary<Vector3Int, List<SurfaceSample>>();

            public SpatialHash(float cellSize) {
                _cellSize = Mathf.Max(0.005f, cellSize);
            }

            public void Add(SurfaceSample sample) {
                var cell = Cell(sample.Position);
                if (!_cells.TryGetValue(cell, out var list)) {
                    list = new List<SurfaceSample>();
                    _cells[cell] = list;
                }
                list.Add(sample);
            }

            public IEnumerable<SurfaceSample> Query(Vector3 position, float radius) {
                int r = Mathf.CeilToInt(radius / _cellSize);
                var center = Cell(position);
                for (int x = center.x - r; x <= center.x + r; x++) {
                    for (int y = center.y - r; y <= center.y + r; y++) {
                        for (int z = center.z - r; z <= center.z + r; z++) {
                            if (_cells.TryGetValue(new Vector3Int(x, y, z), out var list)) {
                                foreach (var sample in list) yield return sample;
                            }
                        }
                    }
                }
            }

            private Vector3Int Cell(Vector3 p) {
                return new Vector3Int(
                    Mathf.FloorToInt(p.x / _cellSize),
                    Mathf.FloorToInt(p.y / _cellSize),
                    Mathf.FloorToInt(p.z / _cellSize));
            }
        }
#endif
    }
}
