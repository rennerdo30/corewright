# Restore ownership

Every slot retains its first original value until restoration is confirmed or
the exact process identity exits. A failed restore does not become a new original
on reapply. Ordinary refused applies remain suppressed until rules change.

Restore failures are reported once per unresolved slot and retried at most once
per minute on the existing sampler. There is no new timer, allocation or scan
when no restore is pending. Successful writes and restores retain their existing
behavior. A deferred backend operation is recoverable and is never cached as a
hard apply failure.

Pause and RevertAll remove successfully restored processes immediately and keep
failed ones, including snapshots and actuator handles. The sampler retries them
even while paused; RevertAll releases finish before a resumed process is evaluated
again. Exited identities are forgotten without attempting another process write.
Shutdown respects the retry deadline and logs each process, slot and original
that could not be restored. It cannot keep retrying after the service exits.
