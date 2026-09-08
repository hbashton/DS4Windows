# Gyro-calibration reader contention, 2026-09-08

An integrated rerun of the reported-issue fixes failed
`OrderedBackgroundWritesRoundTripOpaquePeerAndNewestBias`. This failure was
investigated, not excluded or given a longer timeout. It exposed a pre-existing
calibration-persistence defect; it does not establish an active-input latency
regression or explain every possible timeout.

## Evidence chain

1. The store dequeues each calibration and tries to publish it three times.
   Failure exhausts that record; enqueue success does not mean disk durability.
2. Its own reader used `File.ReadAllBytes`, whose normal read sharing does not
   permit replacement. A held-production-reader regression reliably exhausted
   the three attempts (`gyro-reader-contention-before.trx`: one failed).
3. `FileShare.Read | FileShare.Delete` alone was **insufficient** with the old
   `File.Move(..., overwrite: true)`. The held-reader test still failed; a direct
   production-commit fixture exposed `UnauthorizedAccessException: Access to
   the path is denied` from `FileSystem.MoveFile`
   (`gyro-atomic-replace-diagnostic.trx`).
4. Updating existing records with `File.Replace`, and creating absent records
   with non-overwriting `File.Move`, passed both the direct replacement and
   queued-writer tests. The old reader retains its complete old bytes while
   fresh readers load the newer calibration (`gyro-reader-replace-after.trx`:
   four passed). The original three-second test limits were retained.

Relevant primary contracts:

- [.NET 8 File.ReadAllBytes](https://github.com/dotnet/runtime/blob/v8.0.0/src/libraries/System.Private.CoreLib/src/System/IO/File.cs#L587-L591)
- [Windows rename rules for open destinations](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/ntifs/ns-ntifs-_file_rename_information)
- [ReplaceFile access and failure semantics](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-replacefilew)

## Final behavior

The read handle allows read/delete sharing but still denies in-place writes.
Length is checked on the opened handle, then exactly 49 bytes are read into a
fixed stack buffer. Magic, version, peer identity, digest, finite values and
bias limits remain unchanged. No input report performs file I/O; runtime
calibration binding reads before device activation, while commits keep their
existing rare background write queue.

Publication never deletes the destination first or falls back to an in-place
write. Existence races flow through the existing bounded retry. Tests confirm
that an external non-delete-sharing reader still rejects replacement and that
both old destination and new temporary bytes survive that tested failure.
This is not a promise against all filesystem/driver failures: `ReplaceFile`
has documented failure modes, and external locks or disk faults can still
exhaust the existing best-effort queue.

`gyro-reader-replace-and-allocation.trx`: **157 passed, 0 failed** — nine gyro
file-store tests and all 148 allocation-named tests. Includes held-reader old/new
generation behavior, external sharing denial, short/oversized files, and existing
digest/peer/range validation. All files were disposable fixtures, not the user's
calibration files. Independent review checked the mechanism and scope.
