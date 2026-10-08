# Additional permissions

The program is licensed under the **GNU General Public License v3.0 or later**
([`LICENSE`](LICENSE)). Section 7 of that licence allows the copyright holder to grant
*additional permissions* — terms that relax a GPL requirement without replacing the licence.

This file states the additional permissions granted for Deal or Force. They apply to the
whole program.

    Copyright (C) 2026 Bayte Realization

---

## 1. Linking against the Steamworks SDK

> **Additional permission under GNU GPL version 3 section 7**
>
> If you modify this Program, or any covered work, by linking or combining it with the
> Steamworks SDK by Valve Corporation (or a modified version of that library), containing
> parts covered by the terms of the Steamworks SDK Access Agreement, the licensors of this
> Program grant you additional permission to convey the resulting work.

### Why this is needed

The GPL requires that whoever receives a program can get the complete corresponding source
and rebuild it. The Steamworks SDK is proprietary: its terms forbid redistributing its
source, and they restrict what a recipient may do with it.

Without this permission those two requirements collide, and a build of this game linked
against Steamworks could not be distributed at all — not because anybody objects, but
because the two licences cannot both be satisfied at once.

The permission removes the collision for that one library, and changes nothing else. Every
other GPL obligation stands: the source of **this** program is still supplied, still under
the GPL, and anyone may still study, change and redistribute it.

### What it deliberately does not say

The canonical template for such an exception offers an optional sentence requiring the
Corresponding Source to include the source of the linked library as well. That sentence is
**omitted on purpose**: Valve does not publish the Steamworks source, so including it would
make the permission impossible to comply with and therefore useless.

### Scope

The permission names one library. If the game is later shipped through another storefront
whose SDK is also proprietary — Epic Online Services, GOG Galaxy, a console SDK — that
library needs its **own** paragraph here, written the same way. A permission for Steamworks
does not cover anything else.

### A note on how the SDK is reached

Today the game does not call Steam at all. `godotsteam` is present only in `addons/`, which
is excluded from the repository, and no line of game code talks to it.

When that changes, it does not matter for this permission whether Steamworks is compiled in
or loaded at runtime as a GDExtension. The argument about whether dynamic loading creates a
combined work is one this project does not need to have: the permission is granted either
way.

---

## Adding to this file

Additional permissions can be granted by the copyright holder. As of 2026 that is one
person, so this file can be extended with a commit.

**That stops being true the moment somebody else's contribution is merged.** A contributor
holds copyright in their own work, and from then on a new permission needs their agreement
too. If further permissions are likely to be wanted, grant them **before** the repository
is opened to contributions — not after.

---

*This file puts a standard, widely used mechanism in place and explains what it does. It is
not legal advice. Before money changes hands — a Steam release included — it is worth having
a lawyer read this file, the [asset permissions](README.md#licence),
[third-party notices](docs/THIRD_PARTY_MEDIA.md) and the Steamworks
agreement together.*
