using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace DogShare
{
    // Guest with two or more shared dogs close together: one key press (Q for Commands, the Feed / First aid
    // prompts) reached every shared dog in range, so the game opened each dog's menu in the same frame. The pet
    // menu is a toggle (PetPanel.ToggleExpanded), so the menus replaced / closed each other at once.
    //
    // Fix (guest, shared dogs only): hold back these calls for two frames, then run the action only on the one dog
    // you meant. That's the game's nearest interact object (InteractManager), or if it can't tell, the dog nearest
    // your mouse cursor, or failing that the dog nearest you.
    internal static class OneDog
    {
        struct Req { public PetCharacterController Pc; public string What; public Action<PetCharacterController> Run; }

        static readonly List<Req> _pending = new List<Req>();
        static int _frame = -1;
        static bool _replaying;

        // Called from the prefixes. True = let the game run it now; false = held back.
        internal static bool Allow(PetCharacterController pc, string what, Action<PetCharacterController> run)
        {
            if (_replaying || pc == null) return true;
            if (GuestMirror.Of(pc.npcData) == null) return true;
            int f = Time.frameCount;
            if (_pending.Count > 0 && _pending[0].What != what) _pending.Clear();   // a different action: start over
            foreach (var r in _pending) if (r.Pc.Pointer == pc.Pointer) return false;
            _pending.Add(new Req { Pc = pc, What = what, Run = run });
            _frame = f;
            return false;
        }

        // Every frame from GuestMirror.Update.
        internal static void Tick()
        {
            if (_pending.Count == 0 || Time.frameCount < _frame + 2) return;   // wait 2 frames in case the other dogs' calls come a frame later
            var reqs = new List<Req>(_pending);
            _pending.Clear();
            var pick = Pick(reqs, out string how);
            if (pick.Pc == null) return;
            if (reqs.Count > 1)
            {
                var names = new List<string>();
                foreach (var r in reqs) names.Add(Name(r.Pc));
                DLog.Info("act", $"{pick.What}: one key press reached {reqs.Count} shared dogs [{string.Join(", ", names)}] - opening only '{Name(pick.Pc)}' ({how})");
            }
            _replaying = true;
            try { pick.Run(pick.Pc); }
            catch (Exception e) { DLog.Error("act", $"{pick.What} for '{Name(pick.Pc)}' failed", e); }
            finally { _replaying = false; }
        }

        static Req Pick(List<Req> reqs, out string how)
        {
            how = "only one";
            var alive = reqs.FindAll(r => r.Pc != null && !r.Pc.WasCollected);
            if (alive.Count == 0) return default;
            if (alive.Count == 1) return alive[0];

            try
            {
                var im = InteractManager.instance;
                if (im != null)
                {
                    var nearest = alive.FindAll(r => { try { var io = r.Pc.interactObject; return io != null && im.IsNearestInteractObject(io); } catch { return false; } });
                    if (nearest.Count == 1) { how = "the one the game highlights"; return nearest[0]; }
                }
            }
            catch { }

            Vector2? cursor = Cursor();
            if (cursor.HasValue) { how = "nearest your cursor"; return Closest(alive, cursor.Value); }

            var me = DogShareBehaviour.HostPlayer();
            if (me != null) { how = "nearest you"; return Closest(alive, DogShareBehaviour.Pos(me)); }
            return alive[0];
        }

        static Req Closest(List<Req> reqs, Vector2 to)
        {
            Req best = reqs[0]; float bd = float.MaxValue;
            foreach (var r in reqs)
            {
                float d = (DogShareBehaviour.Pos(r.Pc) - to).sqrMagnitude;
                if (d < bd) { bd = d; best = r; }
            }
            return best;
        }

        static Vector2? Cursor()
        {
            try
            {
                var cam = Camera.main;
                if (cam == null) return null;
                Vector2 screen;
                var mouse = UnityEngine.InputSystem.Mouse.current;
                if (mouse != null) screen = mouse.position.ReadValue();
                else screen = Input.mousePosition;
                Vector3 w = cam.ScreenToWorldPoint(new Vector3(screen.x, screen.y, -cam.transform.position.z));
                return new Vector2(w.x, w.y);
            }
            catch { return null; }
        }

        static string Name(PetCharacterController pc)
        {
            try { return GuestMirror.Of(pc.npcData)?.Data.characterData?.characterName ?? "?"; } catch { return "?"; }
        }
    }

    [HarmonyPatch]
    internal static class OneDogPatches
    {
        [HarmonyPatch(typeof(PetCharacterController), nameof(PetCharacterController.OpenCommands))]
        [HarmonyPrefix, HarmonyPriority(Priority.First)]
        static bool Commands(PetCharacterController __instance, Il2CppSystem.Object __0)
        {
            try { var arg = __0; return OneDog.Allow(__instance, "Commands", pc => pc.OpenCommands(arg)); } catch { return true; }
        }

        [HarmonyPatch(typeof(PetCharacterController), nameof(PetCharacterController.OpenFeedSelect))]
        [HarmonyPrefix, HarmonyPriority(Priority.First)]
        static bool Feed(PetCharacterController __instance, Il2CppSystem.Object __0)
        {
            try { var arg = __0; return OneDog.Allow(__instance, "Feed", pc => pc.OpenFeedSelect(arg)); } catch { return true; }
        }

        [HarmonyPatch(typeof(PetCharacterController), nameof(PetCharacterController.OpenFirstAidSelect))]
        [HarmonyPrefix, HarmonyPriority(Priority.First)]
        static bool FirstAid(PetCharacterController __instance, Il2CppSystem.Object __0)
        {
            try { var arg = __0; return OneDog.Allow(__instance, "First aid", pc => pc.OpenFirstAidSelect(arg)); } catch { return true; }
        }
    }
}
