# Zone entity update, before and after the registry

Reproduce with:

```
dotnet run --project tools/SarnautCore.EntityBench -c Release -- --entities 288 --frames 20000
```

`before` is the loop as it stood at `3badf4c`, kept verbatim in
`LegacyEntityUpdate.cs`. `after` is `SnapshotTimeline.OpenWindow` plus
`EntityRegistry.Reconcile`. Both run the same schedule — a 60Hz render frame and
a 20Hz snapshot — against the same pre-built batches, and both draw into the same
kind of dummy visual, so the only difference between the columns is the loop.

*update* is the per-frame entity pass. *intake* is `SnapshotTimeline.Add`,
charged separately because the new path builds its by-id index there.

288 entities is the subscription an InstLeague1 client gets standing in the
middle of the instance.

## 288 entities, 20 000 frames

| path | update us/frame | update bytes/frame | intake us/frame | intake bytes/frame |
|---|---:|---:|---:|---:|
| before (`3badf4c`) | 120.3 | 16,392 | 5.3 | 19,086 |
| after (registry) | 17.6 | 0 | 1.5 | 2,793 |

The entity pass is **6.8x faster and allocates nothing**. At 60Hz the old pass
alone was 7.2ms/s of frame time and 0.98 MB/s of garbage; it is now 1.1ms/s and
none.

## Scaling, 8 000 frames per point

The point of the rewrite was the shape of the curve, not the constant. `before`
roughly quadruples per doubling of the crowd; `after` roughly doubles.

| entities | before us/frame | after us/frame |
|---:|---:|---:|
| 72 | 28.4 | 12.4 |
| 144 | 46.6 | 19.2 |
| 288 | 111.9 | 18.6 |
| 576 | 419.6 | 37.4 |

## What was quadratic

Per frame, at *n* subscribed entities:

- `SnapshotTimeline.LatestEntityIds` projected and copied a fresh `ulong[]`, and
  the loop read it twice — one for the entity pass and one for the status line.
- `TrySample` re-walked the timeline for every entity and then scanned both
  batches with `FirstOrDefault`, so the entity pass was O(n x batch size).
- The stale sweep called `latestIds.Contains` inside a `Where`, a second full
  scan per tracked entity, into a `ToArray`.

The window is now chosen once per frame, each received tick carries a by-id
index built when it is published, and staleness is a stamp comparison. Intake
dropped as well because `Add` no longer clones a batch it is handed ownership of.

Measured on Windows 11, .NET 10.0.10, Release.
