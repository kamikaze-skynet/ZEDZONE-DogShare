using System;
using HarmonyLib;

namespace DogShare
{
    // The Teammates card's counter (e.g. "0/3") only counts hired teammates: TeamNPCPanel.RefreshHeaderCount
    // skips every row marked isPet. Add the dogs on the card next to it, e.g. "0/3 · Dogs 2/3", on the host and on guests.
    [HarmonyPatch(typeof(TeamNPCPanel), nameof(TeamNPCPanel.RefreshHeaderCount))]
    internal static class TeamCountPatch
    {
        const int DogCap = 3;   // the game's own cap per player (PetSystem.maxPetNumberOffline), and DogShare's MaxDogsPerGuest default

        [HarmonyPostfix]
        static void Postfix(TeamNPCPanel __instance)
        {
            long t0 = Hitch.Start();
            try { Run(__instance); } finally { Hitch.Stop("dog count", t0); }
        }

        static void Run(TeamNPCPanel __instance)
        {
            try
            {
                var text = __instance?.headerCountText;
                var dic = __instance.npcUIDic;
                if (text == null || dic == null) return;
                int dogs = 0;
                foreach (var kv in dic)
                {
                    var ui = kv.Value;
                    if (ui != null && ui.isPet) dogs++;
                }
                if (dogs == 0) return;
                string baseText = text.text ?? "";
                int cut = baseText.IndexOf(" · ", StringComparison.Ordinal);
                if (cut >= 0) baseText = baseText.Substring(0, cut);
                text.text = $"{baseText} · Dogs {dogs}/{Cap()}";
                // the counter box is sized for "0/3": without this the extra text wraps into a column over the rows
                text.horizontalOverflow = UnityEngine.HorizontalWrapMode.Overflow;
                text.verticalOverflow = UnityEngine.VerticalWrapMode.Overflow;
                text.alignment = UnityEngine.TextAnchor.MiddleRight;   // grow to the left, toward the empty space after "Teammates", not over the collapse button
            }
            catch (Exception e) { DLog.ErrorThrottled("teamcount", 60f, "error", "Adding the dog count to the Teammates card failed", e); }
        }

        static int Cap()
        {
            try
            {
                if (GuestMirror.IsGuest()) return Plugin.MaxPerGuest.Value;   // the guest's own setting; the host's is the real cap
                return PetSystem.maxPetNumber > 0 ? PetSystem.maxPetNumber : DogCap;
            }
            catch { return DogCap; }
        }
    }
}
