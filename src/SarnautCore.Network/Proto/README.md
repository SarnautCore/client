# Proto tree — generated copy, not an edit site

`server/proto/sarnaut/v1` is canonical. Every wire change is a `server` pull
request first, and this tree is refreshed from it by `scripts/sync-proto.ps1`
(ADR 0027).

Do not edit anything under `sarnaut/v1` here. The client commits no generated
code — `SarnautCore.Network.csproj` runs `Grpc.Tools` at build time — so a
hand-edited proto compiles green and then mis-parses against a live shard,
which is the exact failure the wire envelope exists to make impossible.

`PROTO_LOCK.sha256` is byte-identical to `server/proto/PROTO_LOCK.sha256`, and
so are the `.proto` files themselves. That is why the do-not-edit notice lives
in this file rather than in a header comment inside each proto: a header would
change the digests the two repositories are required to agree on.

Refresh, then verify:

```powershell
./scripts/sync-proto.ps1                 # from a sibling ../server checkout
./scripts/sync-proto.ps1 -Check          # fails if any copy would change
./scripts/verify-proto-lock.ps1          # offline lock check, no server needed
```
