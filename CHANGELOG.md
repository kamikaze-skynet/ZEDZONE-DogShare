# Changelog

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
