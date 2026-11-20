# Native lazer sample bridge

This change uses only OverlayAPI's subscription to the public
`Player.GameplayState.LastPlayedSamples.ValueChanged` hook. It does not change lazer,
patch methods, use reflection, or capture game audio. It makes no claim about
anti-cheat approval.

## Build and use

Both local changes are required:

- `D:/Code/OverlayAPI`: the producer, targeting .NET 10 and `ppy.osu.Game 2026.1117.0`.
- `D:/Code/KeyASIO.Net`: the receiver and direct one-shot playback adapter.

KeyASIO pins the published OverlayAPI fork through its submodule. Clone with
`--recurse-submodules`, or run `git submodule update --init --recursive` after
checkout. Both applications build against that shared protocol project. The
existing state protocol remains v4; the samples stream has its own v1 codec.

With a .NET 10 SDK and restored packages, build KeyASIO in Release without a runtime
identifier (to avoid the optional custom-runtime import):

```text
dotnet build D:/Code/KeyASIO.Net/src/Apps/KeyAsio/KeyAsio.csproj -c Release
dotnet build D:/Code/OverlayAPI/osu.Game.Rulesets.OverlayAPI/osu.Game.Rulesets.OverlayAPI.csproj -c Release
```

Use the matching lazer version and the producer's merged Release ruleset DLL.
Install it using OverlayAPI's normal rulesets-folder procedure. Installation into
lazer is a separate manual step; this source change does not deploy anything.
Start the modified KeyASIO and enable sync. The log entry `Native lazer sample
session` confirms that native mode has been recognized. Select the desired skin
in KeyASIO; sample fallback follows that selection, not an independently detected
current in-game skin.

KeyASIO hosts a third current-user-only input pipe,
`OverlayAPI.LazerBridge.Samples.v1`. OverlayAPI sends an empty context baseline,
100 ms context heartbeats, and ordered sample-request batches. Sample messages
have their own session, sequence, game clock, mapped beatmap name, and active flag.
The producer never replays sounds observed while disconnected.

## Playback behavior

After a valid native frame from the active lazer process, KeyASIO suppresses its
legacy object-time/autoplay/Relax sounds and physical-key sounds for that process.
It plays only observed one-shot requests with resolvable assets. Storyboard samples
continue through the existing timeline path because the hook does not report them.
The existing combo-break heuristic is also retained separately.

A disconnected or malformed sample channel stays silent instead of reverting to
speculative object-time sounds for the recognized process. Restart KeyASIO if you
want to use an older producer again. The state channels remain independently
required to select lazer and resolve its beatmap catalog. The sample receiver has
a two-second read deadline. Queues are bounded; overflow, inactive sessions,
beatmap changes, and disconnects invalidate pending loads. Stale requests older
than 250 ms are dropped rather than replayed as delayed bursts.

Asset resolution uses the supplied ordered names: mapped beatmap samples when
allowed, selected skin resources, then built-in fallbacks. Names are validated
before local path lookup. Missing samples remain silent. `Volume` is applied as
sample percentage; existing KeyASIO volume overrides still apply. Pan is centered
because the hook provides no balance. The resolver does not promise exact lazer
legacy custom-bank filtering or built-in fallback order.

## Known hook limits

- The hook reports requests/attempts, including some requests where native output
  is muted, missing, paused, or disabled. It does not prove audible playback.
- Reassigning the same array to the bindable may not notify. OverlayAPI cannot
  recover suppressed requests and does not mutate the bindable to force repeats.
- Slider/spinner/hold loops are not exposed. Native mode suppresses their old
  timeline emulation; these continuous sounds are unavailable in this prototype.
- No native pan, pitch/rate, source-object identity, or resolved asset identity is
  supplied. Custom skin and mod behavior can therefore differ.
- Requests are reactive: game processing, IPC, decoding misses and output buffering
  add latency. No measured latency or complete native-audio equivalence is claimed.

## Validation

Check native logs and compare a short map with KeyASIO enabled/disabled:

1. Relax circles: cursor away produces no normal hitsound; a valid hit produces
   one request, with no duplicate from the chart-time queue.
2. Sliders: head, ticks, repeats and tail follow observed requests. Slide/whistle
   loops are intentionally unavailable. Verify Classic tail behavior separately.
3. Use custom beatmap samples, a selected custom skin, then missing samples. Check
   volume and the disable-beatmap-samples setting.
4. Retry, seek, pause/resume, leave gameplay, restart KeyASIO and disconnect the
   samples pipe. Old samples must not play in a new session.
5. Test simultaneous requests and repeated calls using the same sample array; the
   latter remains a documented upstream-hook limitation.

The isolated dependency-free harness at `D:/Code/keyasio-bridge-verification`
links the production protocol, pipe client/receiver and native consumer. All eight
test groups passed under SDK 9, with no compiler warnings or errors. It covers
malformed/Unicode/bounded payloads, immutable snapshots, repeated occurrences,
real named-pipe delivery and reconnect baselines, PID/map/session/sequence checks,
and invalidation of delayed loads. Audio, logging and application context use
stubs; the actual sample resolver and game subscription are not covered by that
compilation. Reconnect was tested through an explicit reset, not by waiting for
the idle deadline. Run it again with:

```text
dotnet run --no-restore --project D:/Code/keyasio-bridge-verification/Verification.csproj
```

Full Release builds succeeded with system-wide .NET SDK 10.0.404. OverlayAPI
compiled against a pristine source-built `ppy.osu.Game 2026.1117.0` package because
that exact channel version was unavailable on nuget.org. Its merged assembly
passed the repository verifier. KeyASIO's direct xUnit runner passed 67 tests,
including the placeholder-DSN startup regression; a responsive application window
was verified after the telemetry fix. In-game audio matching remains unverified.

To restore the exact OverlayAPI dependency on another machine, follow the
producer repository's source-pack fallback for the `2026.1117.0-lazer` tag, then
supply the resulting directory as a local NuGet source alongside nuget.org.
