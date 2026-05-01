# Troubleshooting

Common failure modes and false-positive scenarios. If your problem isn't here, [file a bug](https://github.com/RealWhyKnot/vrc-avatar-qol/issues/new?template=bug_report.yml).

## Menu items don't appear

- **First-time install.** Focus Unity once after dropping in `Editor/`. Unity needs to compile the scripts before the menu items show up.
- **Tool entry greyed out under `GameObject/Avatar QoL`.** The validator skips entries when the selection isn't appropriate. For Weight Sanity Check this means: select a GameObject with a Humanoid Animator (or one in its descendants).

## Weight Sanity Check: "Animator is not Humanoid"

The symmetry check needs Humanoid bone bindings (LeftUpperLeg, RightUpperLeg, Hips). Generic / non-Humanoid rigs aren't supported. Set the rig type to Humanoid in the model importer's *Rig* tab and re-bind the bones.

## Weight Sanity Check: too many false positives

A few common causes:

- **Mesh that bridges sides by design.** Capes, dresses, tails, scarves often have legitimate weights from both Left and Right bones. Add the renderer to the *Exclude renderers* list at the top of the window — those won't be scanned.
- **Center margin too small.** Spine/torso vertices very close to the centerline can swing across the centerline as bind-pose noise. Raise *Center margin* (default 0.02 m) until the false-positives go away.
- **Weight floor too low.** If you're seeing flagged weights below 0.05, raise the *Weight floor* — those are usually rounding / smoothing noise, not real cross-side bleed.
- **Custom bones outside the Humanoid hierarchy.** A bone with no Humanoid ancestor reports as `Unknown` and is skipped by the check. If your custom bones live under a chest/parent that ISN'T tagged Humanoid, the side won't propagate. Re-parent the custom chain under the appropriate Humanoid bone (e.g. `LeftShoulder` for a left-arm prop chain).

## Weight Sanity Check: missing real issues

- **Weight floor too high.** Lower it (try 0.001) to see weights you'd consider negligible.
- **Mesh is non-readable.** Importers can mark a mesh as not-readable for runtime memory savings. The tool can still open `Mesh.vertices` / `GetAllBoneWeights()` in the editor, but if you've pre-baked the mesh to a `MeshCollider` or shipped it as an asset, double-check *Read/Write Enabled* in the importer.
- **Vertex is in the center band.** A vertex in the centerline margin isn't classified as Left or Right and won't be flagged regardless of how it's weighted. If a vertex you expect to be flagged is suspiciously central, lower the *Center margin*.

## Scene-view gizmos don't appear

- Toggle *Show gizmos in Scene view* in the window.
- Make sure Gizmos are enabled in the Scene view itself (top-right toolbar).
- The gizmos are drawn via `SceneView.duringSceneGui`, which only runs while a Scene view is open and visible.

## "Frame" button doesn't move the camera

The *Frame* button calls `SceneView.lastActiveSceneView.LookAt(...)`. If no Scene view has been focused recently, `lastActiveSceneView` may be null. Click into the Scene view once and try again.
