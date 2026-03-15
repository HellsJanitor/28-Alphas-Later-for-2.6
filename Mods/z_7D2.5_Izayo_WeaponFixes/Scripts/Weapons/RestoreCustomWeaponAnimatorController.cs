using HarmonyLib;
using System;
using System.Collections.Generic;
using UnityEngine;

public static class RestoreCustomWeaponAnimatorController
{
    // Toggle this before compiling.
    private const bool DEBUG_LOG = false;

    // Tag from item XML: Tags="IZY,..."
    private static readonly FastTags<TagGroup.Global> IzyTag = FastTags<TagGroup.Global>.GetTag("IZY");

    private static void DLog(string msg)
    {
        if (!DEBUG_LOG) return;
        Log.Out($"[IZY-AnimCtrl] {msg}");
    }

    private static string SafeName(UnityEngine.Object o) => o == null ? "<null>" : o.name;

    private static string SafeItemName(EntityAlive entity)
    {
        try
        {
            var itemClass = entity?.inventory?.holdingItem; // your build: ItemClass
            if (itemClass == null) return "<null item>";
            return itemClass.GetItemName();
        }
        catch
        {
            return "<item name unavailable>";
        }
    }

    private static bool HoldingItemHasIzyTag(EntityAlive entity)
    {
        var itemClass = entity?.inventory?.holdingItem; // ItemClass
        if (itemClass == null) return false;
        return itemClass.HasAnyTags(IzyTag);
    }

    private static string GetTransformPath(Transform t, Transform stopAt = null)
    {
        if (t == null) return "<null>";
        string path = t.name;
        var cur = t.parent;
        while (cur != null && cur != stopAt)
        {
            path = cur.name + "/" + path;
            cur = cur.parent;
        }
        return path;
    }

    private static Animator[] FindAllAnimators(Transform root)
    {
        if (root == null) return Array.Empty<Animator>();
        return root.GetComponentsInChildren<Animator>(true) ?? Array.Empty<Animator>();
    }

    private struct CaptureState
    {
        public int TransformInstanceId;
        public string TransformName;
        public string TransformPath;

        // For every Animator under _transform, store the controller it had in Prefix.
        public List<(int animInstanceId, string animPath, RuntimeAnimatorController controller)> Captured;
    }

    // If vanilla overwrites after Postfix (or later), we can re-apply once on next frame.
    private static readonly Dictionary<int, List<(int animInstanceId, RuntimeAnimatorController controller)>> PendingReapply
        = new Dictionary<int, List<(int, RuntimeAnimatorController)>>();

    private static void DumpAnimators(string label, Transform heldRoot)
    {
        if (!DEBUG_LOG) return;

        if (heldRoot == null)
        {
            DLog($"{label}: heldRoot <null>");
            return;
        }

        var anims = FindAllAnimators(heldRoot);
        DLog($"{label}: heldRoot='{heldRoot.name}' path='{GetTransformPath(heldRoot)}' animCount={anims.Length}");

        for (int i = 0; i < anims.Length; i++)
        {
            var a = anims[i];
            if (a == null)
            {
                DLog($"  [{i}] <null Animator>");
                continue;
            }

            DLog(
                $"  [{i}] anim='{a.name}' animPath='{GetTransformPath(a.transform, heldRoot)}' " +
                $"enabled={a.enabled} activeInHierarchy={a.gameObject.activeInHierarchy} " +
                $"controller={SafeName(a.runtimeAnimatorController)} " +
                $"culling={a.cullingMode} updateMode={a.updateMode} applyRootMotion={a.applyRootMotion} " +
                $"avatar={SafeName(a.avatar)} isHuman={(a.avatar != null ? a.avatar.isHuman.ToString() : "<n/a>")}"
            );
        }
    }

    private static void RestoreAllCapturedControllers(Transform heldRoot, CaptureState state, string phase)
    {
        if (heldRoot == null)
        {
            DLog($"[{phase}] RestoreAll: heldRoot <null> -> return");
            return;
        }

        if (state.Captured == null || state.Captured.Count == 0)
        {
            DLog($"[{phase}] RestoreAll: captured list empty for '{state.TransformName}' -> return");
            DumpAnimators($"[{phase}] Dump (no captured)", heldRoot);
            return;
        }

        var anims = FindAllAnimators(heldRoot);
        if (anims.Length == 0)
        {
            DLog($"[{phase}] RestoreAll: animCount=0 under '{heldRoot.name}'");
            return;
        }

        int restoreCount = 0;

        for (int i = 0; i < anims.Length; i++)
        {
            var anim = anims[i];
            if (anim == null) continue;

            // Find matching captured record by animator instance id if possible.
            RuntimeAnimatorController want = null;
            for (int c = 0; c < state.Captured.Count; c++)
            {
                if (state.Captured[c].animInstanceId == anim.GetInstanceID())
                {
                    want = state.Captured[c].controller;
                    break;
                }
            }

            // If instance id changed (rare), fallback by index if safe.
            if (want == null && i < state.Captured.Count)
                want = state.Captured[i].controller;

            // If still null, skip.
            if (want == null) continue;

            var before = anim.runtimeAnimatorController;
            if (before == want) continue;

            anim.runtimeAnimatorController = want;
            anim.Rebind();
            anim.Update(0f);

            restoreCount++;

            DLog(
                $"[{phase}] RESTORE anim='{anim.name}' animPath='{GetTransformPath(anim.transform, heldRoot)}' " +
                $"before={SafeName(before)} after={SafeName(anim.runtimeAnimatorController)}"
            );
        }

        DLog($"[{phase}] RestoreAll: restored {restoreCount}/{anims.Length} animators under '{heldRoot.name}'");
    }

    // --------------------------------------------------------------------
    // AvatarMultiBodyController (FPV + modern avatar setup)
    // --------------------------------------------------------------------
    [HarmonyPatch(typeof(AvatarMultiBodyController), nameof(AvatarMultiBodyController.SetInRightHand))]
    private static class Patch_AvatarMultiBodyController_SetInRightHand
    {
        private static void Prefix(AvatarMultiBodyController __instance, Transform _transform, ref CaptureState __state)
        {
            var ent = __instance?.entity;

            __state = new CaptureState
            {
                TransformInstanceId = _transform != null ? _transform.GetInstanceID() : 0,
                TransformName = _transform != null ? _transform.name : "<null>",
                TransformPath = GetTransformPath(_transform),
                Captured = new List<(int, string, RuntimeAnimatorController)>()
            };

            if (DEBUG_LOG)
            {
                DLog($"[MultiBody Prefix] entity={(ent != null ? ent.entityId.ToString() : "<null>")} holding='{SafeItemName(ent)}' izyTag={(ent != null && HoldingItemHasIzyTag(ent))}");
                DLog($"[MultiBody Prefix] _transform='{__state.TransformName}' path='{__state.TransformPath}'");
            }

            var anims = FindAllAnimators(_transform);
            DLog($"[MultiBody Prefix] capture animCount={anims.Length}");

            for (int i = 0; i < anims.Length; i++)
            {
                var a = anims[i];
                if (a == null) continue;

                var ctrl = a.runtimeAnimatorController;
                __state.Captured.Add((a.GetInstanceID(), GetTransformPath(a.transform, _transform), ctrl));

                DLog($"[MultiBody Prefix] captured[{i}] anim='{a.name}' animPath='{GetTransformPath(a.transform, _transform)}' controller={SafeName(ctrl)} activeInHierarchy={a.gameObject.activeInHierarchy}");
            }

            if (DEBUG_LOG)
                DumpAnimators("[MultiBody Prefix] Dump", _transform);
        }

        private static void Postfix(AvatarMultiBodyController __instance, Transform _transform, CaptureState __state)
        {
            var ent = __instance?.entity;
            if (ent == null)
            {
                DLog("[MultiBody Postfix] entity <null> -> return");
                return;
            }

            bool isIzy = HoldingItemHasIzyTag(ent);
            DLog($"[MultiBody Postfix] entity={ent.entityId} holdingNow='{SafeItemName(ent)}' izyTag={isIzy} _transform='{SafeName(_transform)}'");

            if (!isIzy)
                return;

            if (DEBUG_LOG)
                DumpAnimators("[MultiBody Postfix] Dump BEFORE restore", _transform);

            RestoreAllCapturedControllers(_transform, __state, "MultiBody Postfix");

            if (DEBUG_LOG)
                DumpAnimators("[MultiBody Postfix] Dump AFTER restore", _transform);

            // Queue one re-apply next frame in case vanilla assigns again after Postfix.
            // Key by transform instance id.
            if (_transform != null && __state.Captured != null && __state.Captured.Count > 0)
            {
                var list = new List<(int, RuntimeAnimatorController)>();
                for (int i = 0; i < __state.Captured.Count; i++)
                    list.Add((__state.Captured[i].animInstanceId, __state.Captured[i].controller));

                PendingReapply[_transform.GetInstanceID()] = list;
                DLog("[MultiBody Postfix] queued one-shot reapply next frame");
            }
        }
    }

    // --------------------------------------------------------------------
    // LegacyAvatarController (third-person / legacy path)
    // --------------------------------------------------------------------
    [HarmonyPatch(typeof(LegacyAvatarController), nameof(LegacyAvatarController.SetInRightHand))]
    private static class Patch_LegacyAvatarController_SetInRightHand
    {
        private static void Prefix(LegacyAvatarController __instance, Transform _transform, ref CaptureState __state)
        {
            var ent = __instance?.Entity;

            __state = new CaptureState
            {
                TransformInstanceId = _transform != null ? _transform.GetInstanceID() : 0,
                TransformName = _transform != null ? _transform.name : "<null>",
                TransformPath = GetTransformPath(_transform),
                Captured = new List<(int, string, RuntimeAnimatorController)>()
            };

            if (DEBUG_LOG)
            {
                DLog($"[Legacy Prefix] entity={(ent != null ? ent.entityId.ToString() : "<null>")} holding='{SafeItemName(ent)}' izyTag={(ent != null && HoldingItemHasIzyTag(ent))}");
                DLog($"[Legacy Prefix] _transform='{__state.TransformName}' path='{__state.TransformPath}'");
            }

            var anims = FindAllAnimators(_transform);
            DLog($"[Legacy Prefix] capture animCount={anims.Length}");

            for (int i = 0; i < anims.Length; i++)
            {
                var a = anims[i];
                if (a == null) continue;

                var ctrl = a.runtimeAnimatorController;
                __state.Captured.Add((a.GetInstanceID(), GetTransformPath(a.transform, _transform), ctrl));

                DLog($"[Legacy Prefix] captured[{i}] anim='{a.name}' animPath='{GetTransformPath(a.transform, _transform)}' controller={SafeName(ctrl)} activeInHierarchy={a.gameObject.activeInHierarchy}");
            }

            if (DEBUG_LOG)
                DumpAnimators("[Legacy Prefix] Dump", _transform);
        }

        private static void Postfix(LegacyAvatarController __instance, Transform _transform, CaptureState __state)
        {
            var ent = __instance?.Entity;
            if (ent == null)
            {
                DLog("[Legacy Postfix] entity <null> -> return");
                return;
            }

            bool isIzy = HoldingItemHasIzyTag(ent);
            DLog($"[Legacy Postfix] entity={ent.entityId} holdingNow='{SafeItemName(ent)}' izyTag={isIzy} _transform='{SafeName(_transform)}'");

            if (!isIzy)
                return;

            if (DEBUG_LOG)
                DumpAnimators("[Legacy Postfix] Dump BEFORE restore", _transform);

            RestoreAllCapturedControllers(_transform, __state, "Legacy Postfix");

            if (DEBUG_LOG)
                DumpAnimators("[Legacy Postfix] Dump AFTER restore", _transform);

            if (_transform != null && __state.Captured != null && __state.Captured.Count > 0)
            {
                var list = new List<(int, RuntimeAnimatorController)>();
                for (int i = 0; i < __state.Captured.Count; i++)
                    list.Add((__state.Captured[i].animInstanceId, __state.Captured[i].controller));

                PendingReapply[_transform.GetInstanceID()] = list;
                DLog("[Legacy Postfix] queued one-shot reapply next frame");
            }
        }
    }

    // --------------------------------------------------------------------
    // One-shot next-frame reapply (covers “vanilla overwrote after Postfix”)
    // --------------------------------------------------------------------
    [HarmonyPatch(typeof(GameManager), nameof(GameManager.Update))]
    private static class Patch_GameManager_Update_OneShotReapply
    {
        private static void Postfix()
        {
            if (PendingReapply.Count == 0) return;

            // Copy keys to avoid modifying while iterating.
            var keys = new List<int>(PendingReapply.Keys);

            for (int k = 0; k < keys.Count; k++)
            {
                int rootId = keys[k];
                var capturedList = PendingReapply[rootId];

                // Find live Transform by instance id is not possible directly,
                // so we reapply only to animators we can still find via Resources.
                // (Expensive; keep DEBUG off normally.)
                var anims = UnityEngine.Resources.FindObjectsOfTypeAll<Animator>();
                int applied = 0;

                for (int i = 0; i < anims.Length; i++)
                {
                    var a = anims[i];
                    if (a == null) continue;

                    // If the animator belongs to a hierarchy whose root has the instance id, match it.
                    // We walk up parents and compare instance ids.
                    var t = a.transform;
                    while (t != null)
                    {
                        if (t.GetInstanceID() == rootId)
                        {
                            // Find matching captured controller by animator instance id.
                            for (int c = 0; c < capturedList.Count; c++)
                            {
                                if (capturedList[c].animInstanceId == a.GetInstanceID())
                                {
                                    var want = capturedList[c].controller;
                                    if (want != null && a.runtimeAnimatorController != want)
                                    {
                                        var before = a.runtimeAnimatorController;
                                        a.runtimeAnimatorController = want;
                                        a.Rebind();
                                        a.Update(0f);

                                        applied++;
                                        if (DEBUG_LOG)
                                            DLog($"[OneShotReapply] anim='{a.name}' before={SafeName(before)} after={SafeName(a.runtimeAnimatorController)}");
                                    }
                                    break;
                                }
                            }
                            break;
                        }

                        t = t.parent;
                    }
                }

                if (DEBUG_LOG)
                    DLog($"[OneShotReapply] rootId={rootId} applied={applied}");

                PendingReapply.Remove(rootId);
            }
        }
    }
}
