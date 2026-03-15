using HLib = HarmonyLib;          // avoid clash with namespace Harmony

namespace Harmony
{
    public class IzayoInit : IModApi
    {
        public void InitMod(Mod _modInstance)
        {
            // --- Harmony setup ---
            var asm = Assembly.GetExecutingAssembly();
            var harmonyId = GetType().FullName;
            var harmony = new HLib.Harmony(harmonyId);

            try
            {
                harmony.PatchAll(asm);
            }
            catch (Exception ex)
            {
                Log.Error($"[IzayoUtils] PatchAll failed: {ex}");
            }
        }
    }
}
