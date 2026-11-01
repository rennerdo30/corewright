# PM-37: service credentials remain a release blocker

Status: design only. Do not publish the SYSTEM service until this is implemented
and independently reviewed. Installer ACL migration deliberately retains Users
read access to ordinary data; it does **not** fix bearer-token disclosure.

## Confirmed exposure

`Corewright.Service/Program.cs` currently writes one bearer token into
`%ProgramData%\Corewright\endpoint.json`. `Api.Guard` accepts it for all API
operations. The data directory grants Users read access. Merely changing the
endpoint ACL breaks unelevated Tray, Dashboard and third-party clients because
`Corewright.Core/EndpointInfo.cs` currently falls back to that machine file.
`ui/vite.config.ts` duplicates this discovery logic.

## Storage and issuance

1. On service startup, create a new administrator-owned `credentials-<random>`
   directory inside the freshly protected machine tree. Create its security
   descriptor atomically: SYSTEM/Administrators full control, Users traverse/read
   directory metadata only, no inherited file-read grant. Never adopt token files
   or rewrite their existing ACLs; old handles would remain valid.
2. Write the machine endpoint into a fresh file with protected DACL
   `SYSTEM:full, Administrators:full`, then atomically replace `endpoint.json`.
   Create the temporary file with that same descriptor, not inherited permissions.
   Mint a new 256-bit token for the service run. An old public token is never
   accepted, even during upgrade. Already disclosed historical bytes cannot be
   made secret by changing an ACL.
3. Enumerate interactive sessions once at startup using WTS, and obtain each
   session's user token/SID with `WTSQueryUserToken`. Use numeric SID strings as
   directory names, never account names, profile paths or request parameters.
   For each logged-on SID, create a fresh directory and endpoint file under the
   administrator-controlled credentials root. Descriptor:
   `SYSTEM:full, Administrators:full, <SID>:read`, protected against inheritance.
   No local user can create/replace objects there or access another SID's file.
4. A public, token-free discovery document names this run's random credentials
   directory and the loopback URL. It is written atomically by SYSTEM, with Users
   read access. Clients append their own Windows SID and `endpoint.json`, validate
   the run ID, and read only that SID's endpoint. Never store privileged files in
   a user's profile, where ancestor junctions and pre-held WRITE_DAC handles are
   untrusted. No endpoint is returned by an unauthenticated HTTP route.

## Events, authorization and client changes

- Extend the existing Windows service lifetime to accept SCM session-change
  notifications. Handle logon/logoff with WTS token lookup and a small serialized
  event queue. Initial enumeration plus notifications covers sessions racing
  startup; deduplicate by session ID and SID. No timer, extra sampler work or
  polling. A disconnect/lock keeps a logged-on session authorized; last logoff
  revokes that SID's token and removes only this run's newly created token file.
- Maintain one random token per authorized SID plus the administrator machine
  token. Store fixed-size decoded bytes and compare in constant time. Snapshot
  token collections only on session events; no allocation per sampler tick.
  Reject malformed lengths before comparing. Stop accepting a token before
  removing its file, and wipe in-memory token buffers on revocation/shutdown.
- The requested policy authorizes logged-on interactive users to change
  machine-wide rules. Per-user files provide isolation and revocation; they do
  not restrict an authorized interactive user to their own processes. If a
  stricter policy is desired, require explicit administrator enrollment of SIDs
  before issuing credentials. Do not silently claim SID separation is API scope
  separation.
- Update `EndpointInfo.TryReadAny` to prefer explicit console overrides, then
  the current SID's service endpoint. Remove fallback to the privileged machine
  token in unelevated clients. Tray re-reads on existing foreground/API events;
  Dashboard re-discovers on navigation/reconnect. Service restart needs no poll.
  Update Vite discovery and integration docs together. Keep HTTP as the only API.
- Keep console mode isolated to its configured scratch/profile directory, with
  a fresh current-user-only endpoint. An invalid explicit override must fail
  closed rather than falling back to the owner's endpoint.
- Use existing JSON source-generation contexts and `LibraryImport` Win32/WTS
  declarations. No reflection serializer, dynamic code generation, new package,
  named pipe or CLI. Never log tokens, Authorization headers or endpoint contents.

## Finite acceptance gate on a disposable Windows runner

- NativeAOT publish Service and Tray, build Dashboard/UI, then install only on
  an isolated runner. Exercise two standard-user SIDs and a separate admin SID.
- Prove one standard user cannot read the machine token or another user's token;
  prove the same user can discover their own token and use the documented HTTP API.
- Prove last-logoff revocation, multiple sessions for one SID, disconnect/lock
  semantics, restart rotation and startup/logon races. Reject old/unknown tokens.
- Pre-create junctions, hard links, hostile ACLs and held WRITE_DAC handles at
  token destinations; no write/read follows them and no old token stays accepted.
- Confirm atomic writes never expose a permissive temporary token file. Search
  runner logs for a synthetic canary credential without printing real tokens.
- Confirm existing sampler frequency and event-driven tray behavior remain
  unchanged; measure NativeAOT idle CPU/private working set before release.

## Win32 contracts

- [CreateFile sharing and reparse handling](https://learn.microsoft.com/windows/win32/api/fileapi/nf-fileapi-createfilew)
- [Directory creation security descriptors](https://learn.microsoft.com/windows/win32/api/fileapi/nf-fileapi-createdirectoryw)
- [Service session notifications](https://learn.microsoft.com/windows/win32/services/service-control-handler-function)
- [WTSQueryUserToken](https://learn.microsoft.com/windows/win32/api/wtsapi32/nf-wtsapi32-wtsqueryusertoken)
