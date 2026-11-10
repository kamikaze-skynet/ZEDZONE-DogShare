# Changelog

## 1.4.1
- Shared dogs now get hungry like any dog and their owner feeds them. The automatic top-up is now the `AutoFeedGuestDogs` setting, off by default (replaces `KeepGuestDogsFed`).
- Feeding a full dog says it isn't hungry and keeps your food, like the game.
- The host's status line logs each guest dog's hunger.

## 1.4.0
- Animation sync for players with DogShare. The game itself doesn't sync petting or feeding animations (they only play on the PC doing it). Now:
  - any dog animation the host plays (sitting to be petted, eating, ...) also plays on every guest's copy of that dog;
  - the petting pose is shown to everyone: the host's, and each guest's (sent through the host).
- Petting a shared dog as a guest now plays your petting pose and makes the dog sit, for everyone.

## 1.3.5
- Fix: petting a shared dog did nothing (the game's petting routine stops on a stand-in). Pressing Pet now asks the host directly; the host applies the affection with the game's cooldown and confirms ("Good dog!").
- Fix: a shared dog's hunger never showed on the guest's PC. The host now sends it with the dog's other details, and confirms each meal ("Bear ate it.").

## 1.3.4
- Fix: the game's dog prompt refuses to build when certain flags are set on the dog, and one is set on a guest's stand-in. For shared dogs DogShare now clears those flags (looked up by name at runtime and logged) only while the prompt, commands, petting, feed and first aid open, then restores them.
- Fix: a guest's order made the dog pop back onto the host's Teammates card (the game's NotifyPetChanged adds any pet to the card). The host now keeps guest dogs off it.
- Logs the stand-in's flag values when a shared dog is attached and when you walk up to it.

## 1.3.3
- Fix: the Commands / Pet / Feed prompt didn't appear when a guest walked up to their dog. Stand-ins don't run the game's interact-range check, so DogShare now runs it for shared dogs every frame.
- Fix: the host kept a given dog on their Teammates card and could still command it. Giving a dog now removes it from the host's team list and card (also cleans up dogs given with older versions); taking it back puts it back.
- Fix: Open Saddlebag on a guest now opens their backpack next to the saddlebag, like the game does.
- More logging: walking into a shared dog's interact range, team list changes on the host.

## 1.3.2
- Fix: guests ignored the host's dog list (expected 19 fields per dog, the host sends 17), so a given dog never got its prompts or Teammates card on the guest's PC. A malformed entry is now logged as an error.

## 1.3.1
- Detailed logging on host and guest: its own `BepInEx\DogShare.log` plus `LogOutput.log`, with a category and timestamp per line, a check of every game hook at start-up, each request and its result, items moving between PCs, and a status summary every 30 s. `Debug.VerboseLog` adds raw requests/replies.

## 1.3.0
- Guests can now feed their dog and give it first aid with the game's own pickers; the item comes out of their backpack and the host feeds / heals the real dog.
- Guests can equip and unequip the collar, vest and saddlebag from the pet panel; items move between their backpack and the dog.
- Guests can open the saddlebag (Open Saddlebag) and move items in and out; changes are saved to the host's world.

## 1.2.0
- Guests now use shared dogs through the game's own UI: the Commands / Pet prompt at the dog, the Teammates card and the orders menu (Follow, Stay, Passive, Defensive, Aggressive), plus rename and skills. No extra keys.
- Removed the F6 menu (the game's build strips Unity's IMGUI, so it couldn't draw).
- Feed and first aid are switched off for shared dogs.

## 1.1.1
- Fix: a guest's dog lost its owner right after being given (the dog AI looks its leader up by id, and the game's lookup only knows local characters). The host now answers that lookup with remote players, so the dog keeps following the guest.
- Fix: guests are now found through the game's remote-player table (NetPlayerSync) instead of scanning for stand-in characters.
- Fix: the guest menu's chat hooks were never switched on in 1.1.0, so F6 couldn't reach the host.

## 1.1.0
- Guest dog menu (F6): Follow / Stay, combat mode, Pet (10 in-game minute cooldown, like the game), Rename, Skills, Release.
- Guests now need DogShare too for the menu. Requests travel over hidden co-op chat lines and replies go only to that guest.

## 1.0.0
- First release.
- Lifts the one-pet co-op limit on the host (back to the single-player cap of 3).
- F7 gives the host's nearest dog to the nearest guest (up to 3 per guest), F8 takes one back.
- Guest dogs re-link to their owner when they rejoin, and are kept fed.
