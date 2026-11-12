# Changelog

## 1.5.0
- New: kennel. Put a dog away and bring the same dog back later, with its name, breed, affection, skills, gear and saddlebag. Stored dogs leave the world and the team, don't count toward the 3-dog limit, and don't get hungry.
  - **F9**: store your dog closest to you (host, guests and single player).
  - **F10**: open the kennel list, then click **Bring back**. The dog appears next to you. You still need a free slot (3 dogs, or the host's MaxDogsPerGuest for guests).
  - The host keeps everyone's stored dogs, one file per world in `BepInEx\config\DogShare.kennel\`. Each player only sees their own. A guest's dogs wait for them while they're offline.
  - Dogs are saved in the game's own save format, and the file is written and checked before the dog is removed. A kennel entry is only deleted once the dog is back. Every step is logged under `[kennel]` in DogShare.log.
  - Guests need DogShare 1.5.0 and a host with 1.5.0.

## 1.4.12
- Fix: a released dog came back. Releasing a dog leaves it in its owner's team list, so the next time the team changed (hiring someone, or taking a dog back with F8) it rejoined and followed again. The host now remembers released dogs (in `BepInEx\config\DogShare.released.txt`), takes them off every team list, stops them rejoining, and leaves them out of give / take back. Adopting the dog again makes it a normal dog again.
- Freeze detector: when a frame takes longer than a quarter of a second, DogShare writes a `perf` line to its log with how long the frame took and how much of that time was DogShare's own work, split by part. Use it to tell whether a freeze comes from DogShare or from something else.
- Guests: the shared dogs' pet and Teammates cards were refreshed every 3 seconds when the host's update arrived, even when nothing had changed. They now refresh only when something they show changes.

## 1.4.11
- Performance fix for big crews of hired AI. Every NPC re-checks who its leader is every update, and each check the game couldn't answer itself went through DogShare, which then searched every character in the world for a matching guest. With lots of hired teammates around that ran hundreds of full searches a second and cost frame rate, on the host and (from a per-frame check) on guests too. DogShare now looks the world up at most twice a second and answers from that list, so the cost no longer grows with the number of NPCs.

## 1.4.10
- Fix for the Teammates dog count from 1.4.9: the text wrapped into a column ("0/3", "·", "Dogs", "2/3") that stuck out above the card and over the first dog's row. It now stays on one line and grows to the left.

## 1.4.9
- The Teammates card's counter now also counts your dogs, for example "0/3 · Dogs 2/3". The game's own number only counts hired teammates and skips dogs. Shown on the host and on guests.

## 1.4.8
- Fix for guests with two shared dogs close together: pressing Q (Commands), Feed or First aid opened both dogs' menus at once, and they closed each other straight away. DogShare now opens only the dog you meant. That's the one the game highlights, or the one nearest your mouse cursor, or failing that the one nearest you. Each time this happens, an `act` log line names the dogs and the one it picked.

## 1.4.7
- Fix for guests with two or more shared dogs: opening one dog's menu (the Commands prompt or its Teammates card) showed the first dog instead, so orders, the saddlebag and the rest went to the wrong dog. For example, Bear's saddlebag wouldn't open because the menu was really Ruby's, and Ruby had no saddlebag. On a guest the game still applies its one-pet co-op rule and treats every pet after the first as "frozen", and the pet menu swaps a frozen pet for the first one. Shared dogs are never frozen on the guest now. The host decides how many dogs there are.

## 1.4.6
- Fix: a guest's dog that got in a car was left behind where the car started, and only came back when its owner went back for it. On the host a guest's character stays where they got in while they drive (the game moves only the car), and the game keeps a riding dog with that spot. DogShare now keeps a guest's riding dog with the car itself and lets it out next to the car when its owner gets out. The host's own dogs were not affected.
- New `car` log lines for a guest's dog getting in and out of a car.

## 1.4.5
- Gift notices for shared dogs: when a guest's dog brings them something ("Bear brought you a dead crow"), the guest now gets the game's notice. The game only showed it on the host's screen; the host no longer gets it for guests' dogs. The gift itself is dropped at the guest's feet as before.
- Dog speech bubbles (the one a dog shows when it brings something back, and others) now show on every guest's screen too.

## 1.4.4
- Fetch works for shared dogs. When a guest throws a tennis ball, the host now tells that guest's dogs about it (the game only did this for the host's own throws), and when the dog brings it back the ball goes into the guest's backpack (or at their feet if it's full) instead of disappearing into the guest's stand-in on the host.
- Logs every step of a fetch (throw, which dog went for it or why none did, ball returned).

## 1.4.3
- Dog sounds are synced for players with DogShare: barks, whines and the happy / soft sounds a dog makes on the host also play on every guest's copy of that dog (the game only played them for the host). Petting a shared dog now also makes its happy sound.

## 1.4.2
- Fix (1.4.1 regression): setting a shared dog's hunger on the guest crashed before the dog's character data existed, so the dog never got set up (no prompt or Teammates card). Hunger is now set after the character data is in place, and new shared dogs start with placeholder character data.
- A failure that repeats every few seconds is now logged at most every 30 s.

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
