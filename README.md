## Sukhoi Matchmaker

[![Tests](https://github.com/PhantomCloak/CMatchDraft/actions/workflows/tests.yml/badge.svg)](https://github.com/PhantomCloak/CMatchDraft/actions/workflows/tests.yml)

Sukhoi is a general purpose matchmaking engine covers wide-variety of real-world use cases.

## Features

- Tickets can dynamically define what/who they ask without needing to pre-define anything
- Tickets can define multiple queries depending on how long they were waiting such as relaxing search criteria
- Tickets can define narrower room sizes depending on how long they were waiting
- Matchmaking supports solo players as well as group of players like Parties
- Matchmaker engine guarantees every player in the room mutually accepts each other
- Backfilling

## Ticket

Ticket is a matchmaking resource can contain one or more players. Ticket holds properties and queries along with min and max bounds of desired match.

#### Example Ticket declaration

```cs
// MaxTicketPatienceInSec: 30
    players: ["p1", "p2", "p3"],
    owner: "p1",
    partyId: "SourKiwi",
    queries: [
                (AtSec: 0,  Query: "+properties.mode:ranked +properties.skill:[900 TO 1100]"),
                (AtSec: 10, Query: "+properties.mode:ranked +properties.skill:[800 TO 1200]"),
                (AtSec: 13, Query: "+properties.mode:ranked +properties.skill:[700 TO 1300]"),
                (AtSec: 18, Query: "+properties.mode:ranked +properties.skill:[500 TO 1500]"),
            ],
     ranges:[
                (AtSec: 0,  Min: 6, Max: 6),
                (AtSec: 8,  Min: 4, Max: 6),
                (AtSec: 30, Min: 2, Max: 6),
            ],
            new() { ["mode"] = "ranked", ["skill"] = 1000 });
```

## Query

Sukhoi has full support for Apache Lucene query syntax — `MUST`, `MUST_NOT`, `SHOULD`, `OR` clauses, ranges and regex expressions. Each ticket holds properties can hold multiple queries at once for given patience schedule. In formed match every member guaranteed to accept each other.

### Example Queries

```cs
+properties.mode:ranked +properties.map:de_dust2 // must have mode ranked and de_dust2
+properties.mode:coop -properties.role:tank      // must be in coop, but must not be another tank
+properties.map:de_dust2 OR properties.map:any   // should be on de_dust2, or fine with any map
+properties.mode:ranked +(properties.map_dust2:T OR properties.map_inferno:T) // Must be ranked, and up for at least one of my maps
+properties.mode:ranked +properties.skill:[2950 TO 3050] // skill must be within 50 of mine
+properties.map:/de_.*|any/ // must have either one of the maps starting with de_* or any
+properties.mode:ranked properties.party:T^10 // must be ranked, strongly prefer players who are in a party
```

## Examples

The following examples can be found in `GameModeTests.cs`

### 5v5 Quickplay

Casual game mode where the player just wants a game quickly, so anyone in the queue is fine as a teammate

Reference: `SimpleFiveVsFiveMatchesAnyTenPlayers`

### 5v5 Ranked

Competitive game mode where every player has an MMR and expects a lobby around their own level. A player who waits longer in the queue starts accepting wider skill gaps

Reference: `RankedFiveVsFiveWithTolerance`

### Role queue

Similar to Overwatch, a team must consist of 1 tank / 2 dps / 2 support. Player picks which roles they can play and every seat is filled once, so no lobby ends up with five dps and no healer

Reference: `CoopRoleQueueFormsOneTankTwoDpsTwoSupport`

### Avoid as teammate

Player can provide a list of people they don't want to be in the same match with. Similar to Dota and Overwatch, players have the choice to avoid playing with certain players

Reference: `CoopMatchesThreePlayersWhoAvoidEachOther`

### Map Selection

Player either picks the maps they want to play or says any map is fine. Both end up in the same match as long as one map is left that everybody agreed on. In the regex variant a player can also ask for any defusal map, e.g. `de_*`

Reference: `MapPreferenceMatchesPickyPlayersWithFlexiblePlayers`, `RegexMapPreferenceMatchesPickyPlayersWithFlexiblePlayers`

### Backfill to lobby

One or more people either left or disconnected from the lobby and the game server creates a backfilling ticket to fill the remaining slots in an ongoing match

Reference: `BackfillSeatsQueuedPlayersIntoARunningLobby`

## Benchmarks

```sh
dotnet run -c Release --project benchmarks -- --sizes=1000,10000 --iters=3
```

Measured cases on R7950X, .NET 8 Runtime

- `chess1v1` — ranked 1v1 chess, bell-curve MMR
- `simple5v5` — simple 5v5, no preference
- `ranked5v5` — ranked 5v5, mixed skill and tolerance
- `modes10` — 5v5 across 10 game modes, one pick each
- `coop3` — co-op role queue, 1 tank / 1 dps / 1 support
- `roleQueue5` — 5v5 seat queue, 1 tank / 2 dps / 2 support
- `mapOr` — map preference, OR clause
- `mapRegex` — map preference, regex

| Scenario     | add 1k | add 10k | add alloc 1k | add alloc 10k | sweep 1k | sweep 10k | sweep alloc 1k | sweep alloc 10k | tickets/s 1k | tickets/s 10k |
| ------------ | -----: | ------: | -----------: | ------------: | -------: | --------: | -------------: | --------------: | -----------: | ------------: |
| `chess1v1`   |  36 ms |  420 ms |        39 MB |        379 MB |    41 ms |    387 ms |          78 MB |          352 MB |       24,075 |        25,839 |
| `simple5v5`  |   8 ms |  106 ms |        15 MB |        148 MB |     5 ms |     66 ms |           8 MB |           75 MB |      186,720 |       149,660 |
| `ranked5v5`  |  36 ms |  410 ms |        37 MB |        374 MB |    10 ms |     85 ms |          24 MB |           98 MB |       92,464 |       116,644 |
| `modes10`    |  30 ms |  334 ms |        29 MB |        303 MB |     5 ms |     60 ms |           8 MB |           76 MB |      169,978 |       165,901 |
| `coop3`      |  39 ms |  423 ms |        35 MB |        337 MB |     4 ms |     78 ms |           8 MB |           75 MB |      246,227 |       127,676 |
| `roleQueue5` |  74 ms |  789 ms |        65 MB |        652 MB |     7 ms |    127 ms |          14 MB |          128 MB |      233,433 |       141,231 |
| `mapOr`      |  57 ms |  637 ms |        46 MB |        465 MB |    17 ms |    187 ms |          18 MB |           90 MB |       58,589 |        53,217 |
| `mapRegex`   |  87 ms |  909 ms |       121 MB |       1.16 GB |     5 ms |     68 ms |           9 MB |           76 MB |      193,907 |       145,490 |

Measured on GitHub Actions runner, `ubuntu-latest` (2 cores, 8 GB RAM), .NET 10

| Scenario (1000 Players) |    add | add alloc |  sweep | sweep alloc | tickets/s |
| ----------------------- | -----: | --------: | -----: | ----------: | --------: |
| `chess1v1`              | 321 ms |     36 MB | 397 ms |       78 MB |     2,516 |
| `simple5v5`             |  37 ms |     13 MB |  25 ms |        8 MB |    39,226 |
| `ranked5v5`             |  92 ms |     33 MB |  34 ms |       24 MB |    29,648 |
| `modes10`               |  73 ms |     26 MB |  17 ms |        9 MB |    59,911 |
| `coop3`                 |  56 ms |     30 MB |  18 ms |        8 MB |    55,038 |
| `roleQueue5`            | 104 ms |     57 MB |  20 ms |       14 MB |    88,502 |
| `mapOr`                 |  72 ms |     41 MB |  41 ms |       18 MB |    24,498 |
| `mapRegex`              | 298 ms |    117 MB |   9 ms |        9 MB |   110,341 |

Benchmark Note: rolequeue5 case uses multiple tickets where each player get N number of tickets per role
