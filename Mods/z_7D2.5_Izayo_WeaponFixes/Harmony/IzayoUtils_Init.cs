using IntegratedConfigs.Harmony;
using IntegratedConfigs.Scripts;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;
using UnityEngine;                // GameObject / Vector3 in DisableOrigin
using HLib = HarmonyLib;          // avoid clash with namespace Harmony

namespace Harmony
{
    public class RebirthUtilsInit : IModApi
    {
        private const bool verbose = false;
        private static bool s_scrapWarmDone = false;

        private static void OnGameStartDone_EagerLoadScrapDB(ref ModEvents.SGameStartDoneData _data)
        {
            try
            {
                // Parse Scrap.xml once per world load (SP is usually fully ready here)
                ScrappingDB.Reload();
                if (verbose) Log.Out("[ScrapDB] Reload completed at GameStartDone.");
            }
            catch (Exception ex)
            {
                if (verbose) Log.Error("[ScrapDB] Reload failed: {0}", ex);
            }
        }

        // *** FIXED SIGNATURE ***
        // ModEvents.PlayerSpawnedInWorld expects ModEventHandlerDelegate<SPlayerSpawnedInWorldData>,
        // which is: void Handler(ref SPlayerSpawnedInWorldData data)
        private static void OnPlayerSpawnedInWorld(ref ModEvents.SPlayerSpawnedInWorldData _data)
        {
            try
            {
                if (s_scrapWarmDone) return;

                // Only do the late ensure on clients; server doesn’t need it
                var cm = SingletonMonoBehaviour<ConnectionManager>.Instance;
                bool isServer = (cm != null && cm.IsServer);
                if (!isServer)
                {
                    ScrappingDB.EnsureTypesResolved("PlayerSpawnedInWorld");
                    if (verbose) Log.Out("[ScrapDB] EnsureTypesResolved at PlayerSpawnedInWorld.");
                }

                s_scrapWarmDone = true;
            }
            catch (Exception ex)
            {
                if (verbose) Log.Error("[ScrapDB] EnsureTypesResolved at spawn failed: {0}", ex);
            }
        }

        private static void OnGameShutdown(ref ModEvents.SGameShutdownData _data)
        {
            ScrappingDB.Invalidate();
            s_scrapWarmDone = false; // reset our one-time flag for the next world/session
            if (verbose) Log.Out("[ScrapDB] Invalidated at GameShutdown.");
        }

        public void InitMod(Mod _modInstance)
        {
            // --- Harmony setup ---
            var asm = Assembly.GetExecutingAssembly();
            var harmonyId = GetType().FullName; // e.g. "Harmony.RebirthUtilsInit"
            var harmony = new HLib.Harmony(harmonyId);

            try
            {
                harmony.PatchAll(asm);
            }
            catch (Exception ex)
            {
                if (verbose) Log.Out(ex.StackTrace ?? "");
            }

            // --- your existing setup (unchanged) ---
            ReflectionHelpers.FindTypesImplementingBase(typeof(IIntegratedConfig), (Type type) =>
            {
                IIntegratedConfig cfg = ReflectionHelpers.Instantiate<IIntegratedConfig>(type);
                Log.Out($"{Globals.LOG_TAG} Registering custom XML {cfg.RegistrationInfo.XmlName}");
                Harmony_WorldStaticData.RegisterConfig(cfg.RegistrationInfo);
            });

            RebirthVariables.loadCommonParticles();
            RebirthVariables.LoadFactionStandings();
            RebirthVariables.LoadCustomParticles();

            RebirthVariables.utilsPath = ModManager.GetMod("zzz_REBIRTH__Utils", true).Path;
            RebirthVariables.ignorePrefabs = XDocument.Load(RebirthVariables.utilsPath + "/IgnorePrefabs.xml");

            if (RebirthVariables.testPurgeDiscovery)
            {
                RebirthVariables.discoveryUnlocks[0] = Tuple.Create(1f, 2f);
                RebirthVariables.discoveryUnlocks[1] = Tuple.Create(2f, 3f);
                RebirthVariables.discoveryUnlocks[2] = Tuple.Create(3f, 4f);
                RebirthVariables.discoveryUnlocks[3] = Tuple.Create(4f, 5f);
                RebirthVariables.discoveryUnlocks[4] = Tuple.Create(5f, 6f);
                RebirthVariables.discoveryUnlocks[5] = Tuple.Create(6f, 7f);
                RebirthVariables.discoveryUnlocks[6] = Tuple.Create(7f, 100f);
            }

            // Move the heavy costs off the first station open:
            //  - Reload at GameStartDone (SP happy path)
            //  - EnsureTypesResolved at PlayerSpawnedInWorld (MP client mapping timing)
            ModEvents.GameStartDone.RegisterHandler(OnGameStartDone_EagerLoadScrapDB);
            ModEvents.PlayerSpawnedInWorld.RegisterHandler(OnPlayerSpawnedInWorld); // <-- now matches delegate
            ModEvents.GameShutdown.RegisterHandler(OnGameShutdown);
        }

        // --------- Compatibility helpers for Harmony 1.x and 2.x ---------

        private static void LogPatchedMethodsCompat(HLib.Harmony harmony, string ownerId, int maxToList = 50)
        {
            try
            {
                var methods = GetPatchedMethodsCompat(harmony);
                if (methods == null)
                {
                    if (verbose) Log.Out("[Rebirth:Init][WARN] Could not retrieve patched methods (Harmony reflection failed).");
                    return;
                }

                int total = 0, mine = 0, listed = 0;

                foreach (var m in methods)
                {
                    total++;
                    var owners = GetOwnersCompat(harmony, m);
                    if (owners == null) continue;
                    if (!owners.Contains(ownerId)) continue;

                    mine++;
                    if (listed < maxToList)
                    {
                        listed++;
                        var decl = m.DeclaringType?.FullName ?? "<unknown>";
                        // if (verbose) Log.Out($"[Rebirth:Init] Patched[{listed}/{mine}]: {decl}.{m.Name}");
                    }
                }

                // if (verbose) Log.Out($"[Rebirth:Init] Patch summary: mine={mine}, allPatchedInAppDomain={total}, listed={listed}");
            }
            catch (Exception ex)
            {
                if (verbose) Log.Out($"[Rebirth:Init][WARN] Could not enumerate patched methods: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static IEnumerable<MethodBase> GetPatchedMethodsCompat(HLib.Harmony harmony)
        {
            var tHarmony = typeof(HLib.Harmony);

            var miStatic = tHarmony.GetMethod("GetPatchedMethods", BindingFlags.Public | BindingFlags.Static);
            if (miStatic != null)
            {
                var res = miStatic.Invoke(null, null) as IEnumerable<MethodBase>;
                if (res != null) return res;
            }

            var miInstance = tHarmony.GetMethod("GetPatchedMethods", BindingFlags.Public | BindingFlags.Instance);
            if (miInstance != null)
            {
                var res = miInstance.Invoke(harmony, null) as IEnumerable<MethodBase>;
                if (res != null) return res;
            }

            return null;
        }

        private static HashSet<string> GetOwnersCompat(HLib.Harmony harmony, MethodBase method)
        {
            var owners = new HashSet<string>();
            var tHarmony = typeof(HLib.Harmony);

            var miGetPatchInfoStatic = tHarmony.GetMethod("GetPatchInfo", BindingFlags.Public | BindingFlags.Static);
            if (miGetPatchInfoStatic != null)
            {
                var info = miGetPatchInfoStatic.Invoke(null, new object[] { method });
                if (info != null)
                {
                    var piOwners = info.GetType().GetProperty("Owners", BindingFlags.Public | BindingFlags.Instance);
                    if (piOwners != null)
                    {
                        var vals = piOwners.GetValue(info) as IEnumerable<string>;
                        if (vals != null) foreach (var id in vals) owners.Add(id);
                        return owners;
                    }
                }
            }

            var miGetPatchInfoInstance = tHarmony.GetMethod("GetPatchInfo", BindingFlags.Public | BindingFlags.Instance);
            if (miGetPatchInfoInstance != null)
            {
                var patchesObj = miGetPatchInfoInstance.Invoke(harmony, new object[] { method });
                if (patchesObj != null)
                {
                    CollectOwnersFromPatchList(patchesObj, "Prefixes", owners);
                    CollectOwnersFromPatchList(patchesObj, "Postfixes", owners);
                    CollectOwnersFromPatchList(patchesObj, "Transpilers", owners);
                    CollectOwnersFromPatchList(patchesObj, "Finalizers", owners);
                    return owners;
                }
            }

            return owners;
        }

        private static void CollectOwnersFromPatchList(object patchesObj, string listPropertyName, HashSet<string> ownersOut)
        {
            var listProp = patchesObj.GetType().GetProperty(listPropertyName, BindingFlags.Public | BindingFlags.Instance);
            if (listProp == null) return;
            var list = listProp.GetValue(patchesObj) as System.Collections.IEnumerable;
            if (list == null) return;

            foreach (var patch in list)
            {
                var ownerProp = patch.GetType().GetProperty("owner", BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)
                              ?? patch.GetType().GetProperty("Owner", BindingFlags.Public | BindingFlags.Instance);
                if (ownerProp == null) continue;
                var val = ownerProp.GetValue(patch) as string;
                if (!string.IsNullOrEmpty(val)) ownersOut.Add(val);
            }
        }

        private void DisableOrigin()
        {
            var _Origin = GameObject.Find("Origin");
            Log.Out($"Rebirth Init:  Origin: {_Origin}");
            if (_Origin != null)
            {
                Log.Out($"Found Origin activeSelf: {_Origin.activeSelf}");
                Origin originObject = _Origin.GetComponent<Origin>();
                if (originObject != null)
                {
                    Log.Out($"Disabling Origin script component...");
                    originObject.enabled = false;
                    originObject.isAuto = false;
                    originObject.OriginPos = Vector3.zero;

                    if (Origin.Instance != null)
                        Log.Out($"Origin Instance found.");
                    else
                        Log.Out($"Origin Instance not set at this time.");
                }
            }
        }
    }
}
