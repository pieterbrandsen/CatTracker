# Sample cache payloads

`items-sample.json` is a **synthetic** payload shaped like the real
`~/Library/Caches/com.apple.findmy.fmipcore/Items.data`. It exists so the parser and the replay
path can be exercised without Apple hardware.

It is a guess at the shape, not ground truth. During the Phase 0 spike
(`setup/macos/spike.sh`), capture a **real, redacted** cache from your Mac and replace this file:

```bash
python3 -m json.tool < ~/Library/Caches/com.apple.findmy.fmipcore/Items.data > items-sample.json
# then edit out the serial numbers, addresses and coordinates you would rather not commit
```

Then compare the field names against `src/CatTracker.Core/FindMyParser.cs` and correct it if
Apple names anything differently on your macOS version. The parser accepts several spellings for
every field and reports what it could not understand, but it cannot invent a mapping it has never
seen.

The second entry deliberately has `"location": null` — a tag nobody has walked past yet. That is
normal, not an error, and the parser must treat it that way.
