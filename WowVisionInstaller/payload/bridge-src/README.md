# SAPI2SR bridge: keep silent parts silent (volume-0 skip)

This is the source of one change baked into `../sapi2sr-payload.zip`
(`x64/sapi2sr_engine.dll`). It answers WowVision PR #84: the group health
monitor, and anything else that gates speech with `<volume level="0">`, was
read aloud in full on the NVDA/SAPI2SR bridge.

## The problem

SAPI parses an utterance's inline markup and hands the engine a list of text
fragments, each with its own volume. A real SAPI voice plays a fragment at
`<volume level="0">` as silence. The stock SAPI2SR engine glued every
fragment's text together and sent it all to NVDA, so the monitor's silent gate
copies were spoken: NVDA said "1 1 1 full" where a real voice says "1 full",
and a 3-second repeat would read every member's number, hurt or not.

## The change

`patch_sapi2sr_x64_volume.py` adds one check to the engine's fragment loop (the
same loop the earlier bookmark-skip patch uses): a fragment whose SAPI `Volume`
is 0 is skipped, exactly as a bookmark fragment is. If every fragment is
silent, the engine sends nothing. Nothing else changes; `<volume level="50">`
and every other value still play. Verified offline (`volume-test/`, a recording
NVDA stand-in): full health -> "1 full", a hurt repeat names only hurt members,
an all-silent line sends no call. Confirmed in WoW on the TBC client.

It builds on the shipped x64 engine (bookmark fix + speakText route + `skuint`
interrupt, pin `5A76A572...`) and produces pin
`CCCD06D1CC0996735FD51ABD731932E10132B38ED3A7FF4F1CB9FB0666DE913F`, which is
added to the bundled `sku-nvda-voice-sign.ps1`. x86 is unchanged (the monitor
is a Retail/Forever, x64, feature).

## Rebuilding the payload

    py patch_sapi2sr_x64_volume.py <unsigned A+B+C engine> sapi2sr_engine.dll
    # replace x64/sapi2sr_engine.dll in the payload, add the printed pin to
    # sku-nvda-voice-sign.ps1, re-zip. The installer signs the DLL on the
    # machine (Blizzard's loader gate refuses unsigned engine DLLs).

The bridge payload is shared byte-for-byte with the Sku installer, so this same
engine + pin belongs in Sku's payload too; keep the two in step.

## Pitch was investigated and is NOT shipped (here is why)

PR #84 also asked whether the member's health pitch could reach NVDA (a real
SAPI voice already plays it from `<pitch absmiddle>`). We built and proved it
end to end on the bridge: the engine records the audible fragment's pitch, rate
and volume and the screen-reader proxy turns them into SSML `<prosody>` for
NVDA's own `speakSsml`. The SSML is well-formed and NVDA accepts every call.

But on NVDA 2026.3 the prosody has no audible effect, with **both** Eloquence
and eSpeak. Tested by sending SSML straight to NVDA, bypassing the game:
`<break>` and `<say-as>` are honoured (so NVDA does parse the SSML), while
`<prosody pitch/rate/volume>` is dropped. So NVDA does not apply externally
supplied SSML prosody, and the bridge cannot deliver the pitch to a screen
reader regardless of how it is sent. The volume-0 skip above is the part that
works, and it is what this change ships. If a future NVDA honours external
`<prosody>`, the proxy side can be revisited then.
