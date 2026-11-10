using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using HarmonyLib;
using Il2CppInterop.Runtime;

namespace DogShare
{
    // PetCharacterController.OnEnterPlayerInteractRange (the game's Commands / Pet / Feed prompt) refuses to
    // build the prompt when any of four bool fields on the dog is set (offsets 0x6cd, 0x6d8, 0x9d4, 0x9e9 in
    // game 1.0.13). On a guest's stand-in one of them is set. We look the fields up by offset at runtime
    // (names and types are logged), and for shared dogs only, clear them while the prompt and its handlers
    // run, then put them back.
    internal static class PromptFlags
    {
        static readonly int[] Offsets = { 0x6cd, 0x6d8, 0x9d4, 0x9e9 };
        internal static readonly List<(string name, int off)> Fields = new List<(string, int)>();
        static bool _resolved;

        const int IL2CPP_TYPE_BOOLEAN = 0x02;

        internal static void Resolve()
        {
            if (_resolved) return;
            _resolved = true;
            try
            {
                var found = new Dictionary<int, string>();
                IntPtr klass = Il2CppClassPointerStore<PetCharacterController>.NativeClassPtr;
                while (klass != IntPtr.Zero)
                {
                    IntPtr iter = IntPtr.Zero, f;
                    while ((f = IL2CPP.il2cpp_class_get_fields(klass, ref iter)) != IntPtr.Zero)
                    {
                        int off = (int)IL2CPP.il2cpp_field_get_offset(f);
                        if (Array.IndexOf(Offsets, off) < 0) continue;
                        string name = Marshal.PtrToStringAnsi(IL2CPP.il2cpp_field_get_name(f));
                        int type = IL2CPP.il2cpp_type_get_type(IL2CPP.il2cpp_field_get_type(f));
                        if (type != IL2CPP_TYPE_BOOLEAN) { DLog.Warn("guest", $"Prompt flag at 0x{off:x} is '{name}' but not a bool (type {type}) - leaving it alone"); continue; }
                        found[off] = name;
                    }
                    klass = IL2CPP.il2cpp_class_get_parent(klass);
                }
                foreach (var off in Offsets)
                {
                    if (found.TryGetValue(off, out var n)) Fields.Add((n, off));
                    else DLog.Warn("guest", $"No bool field at 0x{off:x} on PetCharacterController - game version changed?");
                }
                DLog.Info("guest", "Prompt gate fields: " + string.Join(", ", Fields.ConvertAll(x => $"{x.name}@0x{x.off:x}")));
            }
            catch (Exception e) { DLog.Error("guest", "Couldn't look up the prompt gate fields", e); }
        }

        internal static string Describe(PetCharacterController pc)
        {
            Resolve();
            var parts = new List<string>();
            foreach (var (name, off) in Fields)
            {
                try { parts.Add($"{name}={(Marshal.ReadByte(pc.Pointer + off) != 0 ? "TRUE" : "false")}"); } catch { }
            }
            return string.Join(", ", parts);
        }

        // Clear the set flags; returns what to restore.
        internal static List<(int off, byte val)> Clear(PetCharacterController pc, string where)
        {
            Resolve();
            List<(int, byte)> saved = null;
            foreach (var (name, off) in Fields)
            {
                byte v = Marshal.ReadByte(pc.Pointer + off);
                if (v == 0) continue;
                (saved ??= new List<(int, byte)>()).Add((off, v));
                Marshal.WriteByte(pc.Pointer + off, 0);
                DLog.Throttle("clr|" + where + "|" + name, 30f, "guest", $"{where}: '{name}' was set on the stand-in - cleared it while the game builds the prompt");
            }
            return saved;
        }

        internal static void Restore(PetCharacterController pc, List<(int off, byte val)> saved)
        {
            if (saved == null || pc == null) return;
            foreach (var (off, v) in saved) Marshal.WriteByte(pc.Pointer + off, v);
        }
    }

    // For shared dogs on a guest: run the prompt and its handlers with the gate flags cleared.
    [HarmonyPatch]
    internal static class PromptGatePatches
    {
        static bool Mirror(PetCharacterController pc) => pc != null && GuestMirror.Of(pc.npcData) != null;

        static void Pre(PetCharacterController pc, string where, out List<(int, byte)> state)
        {
            state = null;
            try { if (Mirror(pc)) state = PromptFlags.Clear(pc, where); } catch (Exception e) { DLog.Throttle("gateerr", 30f, "error", "Clearing prompt flags failed: " + e.Message); }
        }
        static void Post(PetCharacterController pc, List<(int, byte)> state)
        {
            try { PromptFlags.Restore(pc, state); } catch { }
        }

        [HarmonyPatch(typeof(PetCharacterController), nameof(PetCharacterController.OnEnterPlayerInteractRange))]
        [HarmonyPrefix] static void EnterPre(PetCharacterController __instance, out List<(int, byte)> __state) => Pre(__instance, "prompt", out __state);
        [HarmonyPatch(typeof(PetCharacterController), nameof(PetCharacterController.OnEnterPlayerInteractRange))]
        [HarmonyPostfix] static void EnterPost(PetCharacterController __instance, List<(int, byte)> __state) => Post(__instance, __state);

        [HarmonyPatch(typeof(PetCharacterController), nameof(PetCharacterController.OpenCommands))]
        [HarmonyPrefix] static void CmdPre(PetCharacterController __instance, out List<(int, byte)> __state) => Pre(__instance, "commands", out __state);
        [HarmonyPatch(typeof(PetCharacterController), nameof(PetCharacterController.OpenCommands))]
        [HarmonyPostfix] static void CmdPost(PetCharacterController __instance, List<(int, byte)> __state) => Post(__instance, __state);

        [HarmonyPatch(typeof(PetCharacterController), nameof(PetCharacterController.PetInteract))]
        [HarmonyPrefix] static void PetPre(PetCharacterController __instance, out List<(int, byte)> __state) => Pre(__instance, "pet", out __state);
        [HarmonyPatch(typeof(PetCharacterController), nameof(PetCharacterController.PetInteract))]
        [HarmonyPostfix] static void PetPost(PetCharacterController __instance, List<(int, byte)> __state) => Post(__instance, __state);

        [HarmonyPatch(typeof(PetCharacterController), nameof(PetCharacterController.OpenFeedSelect))]
        [HarmonyPrefix] static void FeedPre(PetCharacterController __instance, out List<(int, byte)> __state) => Pre(__instance, "feed", out __state);
        [HarmonyPatch(typeof(PetCharacterController), nameof(PetCharacterController.OpenFeedSelect))]
        [HarmonyPostfix] static void FeedPost(PetCharacterController __instance, List<(int, byte)> __state) => Post(__instance, __state);

        [HarmonyPatch(typeof(PetCharacterController), nameof(PetCharacterController.OpenFirstAidSelect))]
        [HarmonyPrefix] static void AidPre(PetCharacterController __instance, out List<(int, byte)> __state) => Pre(__instance, "first aid", out __state);
        [HarmonyPatch(typeof(PetCharacterController), nameof(PetCharacterController.OpenFirstAidSelect))]
        [HarmonyPostfix] static void AidPost(PetCharacterController __instance, List<(int, byte)> __state) => Post(__instance, __state);
    }

    // Host: PetPanel.NotifyPetChanged puts any pet on the local Teammates card (no owner check), so a guest's
    // dog popped back onto the host's card whenever it changed (e.g. after a guest's order). Take it off again.
    [HarmonyPatch(typeof(PetPanel), nameof(PetPanel.NotifyPetChanged))]
    internal static class HostCardPatch
    {
        [HarmonyPostfix]
        static void Postfix(GOAPNPCData __0)
        {
            try
            {
                if (__0 == null || !ZedZone.Net.NetworkManager.isOnline || ZedZone.Net.NetworkManager.isClient) return;
                if (PetSystem.IsStrayPet(__0)) return;
                var host = DogShareBehaviour.HostPlayer();
                string hostId = DogShareBehaviour.IdOf(host);
                if (string.IsNullOrEmpty(hostId) || string.IsNullOrEmpty(__0.leaderCharacterGUID) || __0.leaderCharacterGUID == hostId) return;
                TeamNPCPanel.instance?.RemoveNPC(__0);
                DLog.Throttle("card|" + __0.npcGUID, 30f, "host", $"Kept guest dog {DLog.S(__0.npcGUID)} off my Teammates card after a change");
            }
            catch (Exception e) { DLog.Throttle("carderr", 30f, "error", "Host card fix failed: " + e.Message); }
        }
    }
}
