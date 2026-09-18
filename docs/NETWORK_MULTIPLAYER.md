# Network multiplayer

*Русская версия: [NETWORK_MULTIPLAYER_RU.md](NETWORK_MULTIPLAYER_RU.md)*

## Getting started

1. Everybody needs the same game version and the same set of mods.
2. The host opens `Multiplayer · LAN / IP`, picks a country tag, leaves the UDP port at
   `7270` and presses `HOST`.
3. The others type a name, a distinct country tag, the host's IPv4 and the same port, then
   press `JOIN`.
4. Once at least two players are registered, the host presses `START CAMPAIGN`.

On an ordinary LAN, use the host's local IPv4. On Radmin VPN or Hamachi, use the IPv4 the
virtual network shows. To connect directly over the internet without a VPN, the host's
router has to forward UDP `7270` to their machine, and Windows Firewall has to allow
incoming UDP for the game.

## The synchronisation model

The game runs **lockstep**: the machines exchange orders to the world rather than the state
of it, and each runs the same simulation over the same seed. The world as a whole is only
sent when somebody joins and when something is being repaired.

The host is the only authority:

- a client sends only a serialised `ICampaignCommand`, never state;
- the host checks `IssuerId` against the country assigned to that player and **rejects
  impersonation** — a rejected order returns a refusal to its sender rather than vanishing
  silently;
- an accepted command is reliably broadcast to everybody together with **the day it was
  given on**;
- the calendar, the pause and the speed belong to the host;
- joining at the start and joining late both receive a compressed campaign snapshot;
- connected players' countries are marked as human seats and are not run by the AI.

### An order is applied on the day it was given

That is the whole point of lockstep. A client a few days behind used to apply an order the
moment it arrived — to a world several days younger than the one the order was given in. The
host raises a tax on day 400, the client on day 396, and from then on the worlds quietly
differ: there is no error, the screen looks fine, and the divergence accumulates.

Now the order carries its day. A client that has not reached that day yet **holds the order
in a queue** and applies it between ticks, exactly when it arrives there. A client that has
already passed the day applies it at once; that happens when things have desynchronised, and
the next section is what fixes it.

### Catching up, the checksum and resync

- A client that has fallen behind catches up by one day per message, at most eight days at a
  time, and shows `CATCHING UP · N day(s) behind the host`.
- More than **20 days** behind, it stops catching up and asks for the world again: catching
  up for ever is worse than freezing once.
- Every **10 days** the host broadcasts a checksum of the world (`CampaignFingerprint`) — a
  fold over countries, formations, wars and provincial control, ordered by identifier rather
  than by position in a list.
- A client compares it only against the same day. A mismatch is `DESYNCHRONISED on day N`
  and a snapshot request.
- The requests are rate-limited: a client that has fallen behind will still be behind by the
  next message, and a world this size is not resent several times a second.

### Why determinism holds

Determinism here is the foundation, not a consequence. Three standing checks in
`Tools/SimBench` hold it up:

| Check | What it asserts |
|---|---|
| `the same seed gives the same world twice` | Two runs of one seed match bit for bit over 90 days. |
| `two worlds in one process do not read each other's notes` | A campaign run beside another matches the one run alone. |
| `every order a player can give crosses the wire` | Every implementation of `ICampaignCommand` survives the codec. |

The second deserves a word of its own, because it catches a failure even the checksum cannot
see. Several systems cache expensive answers — who borders whom, what each country's army is
worth, how far apart the capitals are — and used to tag that cache with a day, a map revision
or a country count. Each of those tags is correct for exactly one world. But a client
rebuilding from a snapshot holds **two**: the new campaign and the old one the garbage
collector has not taken yet. Whichever ticks first sets the tag; the second reads somebody
else's answers.

Hence the rule: **any cache that lives between calls is tagged with a reference to the
campaign it describes.** Add such a cache and you add the tag, or the check fails.

## Limits of the current stage

- losing the host ends the session: there is no host migration yet;
- NAT traversal/relay and a public server browser are not implemented;
- before a long campaign, do a short connection test and compare the mod sets.

## Related files

- `Scripts/NetworkMultiplayerUI.cs` — the lobby, ENet, RPC, country assignment, the
  authoritative calendar, deferred orders, the checksum and resync;
- `Scripts/Simulation/NetworkCommandCodec.cs` — the allowlist and command serialisation;
  implementations of `ICampaignCommand` are found by reflection, so a new command reaches the
  wire by itself;
- `Scripts/Simulation/CampaignFingerprint.cs` — one number for the whole world;
- `Scripts/Simulation/SaveSystem.cs` — the snapshot for joining and for repair;
- `Scripts/Simulation/SimulationCore.cs` — the command queue and its interceptor.
