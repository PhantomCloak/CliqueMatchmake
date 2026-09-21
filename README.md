## CliqueMatchmaker

[![Tests](https://github.com/PhantomCloak/CliqueMatchmaker/actions/workflows/tests.yml/badge.svg)](https://github.com/PhantomCloak/CliqueMatchmaker/actions/workflows/tests.yml)
[![Benchmark](https://github.com/PhantomCloak/CliqueMatchmaker/actions/workflows/benchmark.yml/badge.svg)](https://github.com/PhantomCloak/CliqueMatchmaker/actions/workflows/benchmark.yml)


CliqueMatchmaker is a general purpose matchmaking engine covers wide-variety of real-world use cases.

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

CliqueMatchmaker has full support for Apache Lucene query syntax — `MUST`, `MUST_NOT`, `SHOULD`, `OR` clauses, ranges and regex expressions. Each ticket holds properties can hold multiple queries at once for given patience schedule. In formed match every member guaranteed to accept each other.

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

Similar to Overwatch, a team must consist of 1 tank / 2 dps / 2 support

Reference: `CoopRoleQueueFormsOneTankTwoDpsTwoSupport`

### Avoid as teammate

Player can provide a list of people they don't want to be in the same match with. Similar to Dota and Overwatch, players have the choice to avoid playing with certain players

Reference: `CoopMatchesThreePlayersWhoAvoidEachOther`

### Map Selection

Player either picks the maps they want to play or says any map is fine. Both end up in the same match. In the regex variant a player can also ask for any defusal map, e.g. `de_*`

Reference: `MapPreferenceMatchesPickyPlayersWithFlexiblePlayers`, `RegexMapPreferenceMatchesPickyPlayersWithFlexiblePlayers`

### Backfill to lobby

One or more people either left or disconnected from the lobby and the game server creates a backfilling ticket to fill the remaining slots in an ongoing match

Reference: `BackfillSeatsQueuedPlayersIntoARunningLobby`

## Benchmarks

```sh
dotnet run -c Release --project benchmarks -- --sizes=1000,10000 --iters=3
```

### Scenarios

- `chess1v1` — ranked 1v1 chess, bell-curve MMR
- `simple5v5` — simple 5v5, no preference
- `ranked5v5` — ranked 5v5, mixed skill and tolerance
- `modes10` — 5v5 across 10 game modes, one pick each
- `coop3` — co-op role queue, 1 tank / 1 dps / 1 support
- `roleQueue5` — 5v5 seat queue, 1 tank / 2 dps / 2 support
- `mapOr` — map preference, OR clause
- `mapRegex` — map preference, regex

<br>

Measured on GitHub Actions runner, `ubuntu-latest` (2 cores, 8 GB RAM), .NET 10

| Scenario     | Players |     add | add alloc |  sweep | sweep alloc |
| ------------ | ------: | ------: | --------: | -----: | ----------: |
| `chess1v1`   |      1k |  321 ms |     36 MB | 397 ms |       78 MB |
| `chess1v1`   |     10k |  835 ms |    325 MB | 722 ms |      351 MB |
| `simple5v5`  |      1k |   37 ms |     13 MB |  25 ms |        8 MB |
| `simple5v5`  |     10k |  187 ms |    129 MB | 137 ms |       74 MB |
| `ranked5v5`  |      1k |   92 ms |     33 MB |  34 ms |       24 MB |
| `ranked5v5`  |     10k |  554 ms |    321 MB | 146 ms |       96 MB |
| `modes10`    |      1k |   73 ms |     26 MB |  17 ms |        9 MB |
| `modes10`    |     10k |  424 ms |    260 MB | 104 ms |       75 MB |
| `coop3`      |      1k |   56 ms |     30 MB |  18 ms |        8 MB |
| `coop3`      |     10k |  515 ms |    296 MB | 136 ms |       74 MB |
| `roleQueue5` |      1k |  104 ms |     57 MB |  20 ms |       14 MB |
| `roleQueue5` |     10k | 1000 ms |    575 MB | 224 ms |      125 MB |
| `mapOr`      |      1k |   72 ms |     41 MB |  41 ms |       18 MB |
| `mapOr`      |     10k |  729 ms |    411 MB | 342 ms |       89 MB |
| `mapRegex`   |      1k |  298 ms |    117 MB |   9 ms |        9 MB |
| `mapRegex`   |     10k | 1663 ms |   1.12 GB | 150 ms |       75 MB |
