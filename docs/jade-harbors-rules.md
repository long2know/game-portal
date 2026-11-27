# Jade Harbors

Set piers on the banks, build routes to new sites, and turn piers into warehouses.
Three seats play: three guests, or one guest with two bots. Reach **8 points** first,
or have the highest score when turn 120 ends. Resources and points stay in the
match and have no cash value.

## Canal board

The neutral central canal is surrounded by six producing hexes and six outer
spokes, one beyond each inner hex. Their distinct corners form **48 sites** and
their distinct sides form **60 routes**. The map is fixed. Each resource has three
producing hexes with the same combined probability of rolling their numbers.

| Resource ID | Material | Producing numbers |
| --- | --- | --- |
| 0 | Timber | 3, 5, 6 |
| 1 | Clay | 8, 9, 11 |
| 2 | Grain | 4, 6, 10 |
| 3 | Stone | 4, 8, 10 |

Tile IDs start with the canal at 0, then the inner ring (1–6) and outer
spokes (7–12), each clockwise from the east. Site IDs sort by lattice `y`
then `x`; route IDs sort by their smaller and then larger endpoint ID.
The integer centers of the pointy-top hexes are `(2q + r, 3r)` in axial `(q, r)`
order. Each tile lists six site IDs clockwise from its top corner. Draw at
`(x × √3/2, y × 1/2)` for equally sized sides.

## Opening

Seats 0, 1, 2, 2, 1, 0 each place a free pier followed by a free route incident
to that pier. Occupied sites can never share the ends of a route: leave at
least one open site between piers or warehouses. A second opening pier
immediately collects one material from each neighboring producing hex,
regardless of its number. The first pier and the routes collect nothing.
After setup, seat 0 takes the first regular turn.

## Take a turn

1. **Roll once.** Add the faces of two independent six-sided dice. Every matching
   hex gives one material to each adjacent pier and two to each adjacent
   warehouse, even if they belong to the other seats. A player can hold up
   to 12 of each material (48 total); excess production is lost. A total of
   **7** produces nothing.
2. **Build or exchange in any order.** New routes must join your existing pier,
   warehouse, or route at at least one endpoint. A route may end at a rival
   building but cannot continue through it. A new pier must touch one of
   your routes and obey the same spacing rule as setup. A warehouse replaces
   one of your piers. You may exchange once per turn: pay **3** of one
   material for **1** of a different material, or pay **2** on a roll of 7.
   The exchange cannot take a material already at capacity.
3. **End the turn.** Passing after the roll is always allowed, even if you
   cannot build or exchange. Seats rotate 0, 1, 2.

| Construction | Cost | Points |
| --- | --- | --- |
| Route | 1 timber + 1 clay | 0 |
| Pier | 1 timber + 1 clay + 1 grain | 1 |
| Warehouse upgrade | 2 stone + 1 grain | 2 total (adds 1) |

No separate piece inventory limits building: each of the 48 sites holds at most
one building and each of the 60 routes has at most one owner. Pieces cannot
move or be removed. Reaching 8 points ends the game immediately. Otherwise,
ending regular turn 120 settles by points; an equal highest score is a tie,
with no material, route-length, or seat-order tiebreaker.

## Saves and projections

The server saves the version-1 position and the exact private dice pairs for
turns 1–120. A resumed match uses the same pairs. A guest receives public
pieces, the last rolled pair (or `null` before the first roll), points, and
other seats' **total** material counts. Only the recipient's holdings are
split by material; future rolls and checkpoints never leave the server.
The active seat gets its current legal options. Finished matches and closed
tables have no active seat or legal actions. See the [mounted API](portal-api.md#jade-harbors).

Bots use their own guest-equivalent view, favor high-producing sites and
materials they lack, and build toward reachable sites or trade for materials
they can use. They do not see opponents' material breakdown or future dice.
One bot advance can cross at most two regular turns. With at most 48 materials
and a minimum construction cost of two, each such turn can contain at most
24 constructions, one roll, one exchange, and one pass. Including all 12
setup actions gives a conservative maximum of 66 commands per advance.

## Design references

- [JSettlers2, pinned revision](https://github.com/jdmonin/JSettlers2/tree/7a756baac6ad675f0930605bfb3a8e1bd7a796a5):
  inspiration for separating authoritative rules, networking, and bots.
- [Catanatron, pinned revision](https://github.com/bcollazo/catanatron/tree/ecf931181b9a65bb4116a2153fb78c16f1438e00):
  inspiration for enumerating legal actions and testing bounded, seeded games.

Both references are GPL-3.0 at the repository level. Jade Harbors is an
original Jade Court game, not a compatible reimplementation. It does not
include their code, art, or rule text, depend on Java or Python, or implement
accounts, robbers, player-to-player trading, longest-road bonuses, card packs,
or expansions.
