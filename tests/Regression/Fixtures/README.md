# Compatibility fixtures

Captured from the unmodified v0.7.0 Release assembly at source commit
`f6a870ab92b16f871cd4ef3b76c1fd30af60b878`, using .NET SDK 9.0.304.

- `public-api.txt`: exported type and member signatures.
- `harmony-patches.txt`: patch types and Harmony target attributes.
- `harmony-registration-order.txt`: original metadata enumeration order of patch types.
- `protocol-constants.txt`: enum values, envelope sizes, versions, and timeouts.
- `snapshot-v5.bin`: original raw payload for synthetic, unsorted runs/acts/rooms,
  opaque map/player blobs, UTF-8 text, signed counters and multi-byte integers.
- `compacted-v5.bin`: the same payload after original blob garbage collection.
- `migrated-v4.bin`: the original v4-to-v5 migration of synthetic legacy JSON.
- `control-envelope.bin` and `direct-envelope.bin`: original envelopes around
  synthetic payload bytes. They exercise framing rather than native game saves.

No real saves, game assemblies, or game assets are included. Update these files
only from an independently verified baseline; never recapture from the code under
review merely to make a regression check pass.
