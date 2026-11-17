# Loupe

A fast image browser for quality control of archive scans — Bad Ideas Works, built on the
Striplight PRO engine (libjpeg-turbo + the Striplight tone methods).

## What it does

- **Browse** drives, external disks and network shares (pin the ones you use). Thumbnails are
  cached locally, decoded at reduced size straight from the JPEG, so big folders open fast.
  Thumbnail size or a fixed number of columns; Ctrl+wheel changes either.
- **View** with zoom / pan / 1:1, next / previous with read-ahead.
- **Correct** tone (all Striplight methods, rescue curves, strength), sharpen, negative,
  rotate and mirror. As in Striplight PRO: a **global** correction for the whole folder and
  **own** corrections for single images (orange), locked against later global changes. Thumbnails
  and the viewer show the preview; nothing touches the files until **Save changes**. Hold B for "Before".
  - The **original is kept** (outside the archive folders, under `%LOCALAPPDATA%\Loupe\Originals`),
    so "Restore original" always returns the file as it was before Loupe touched it.
  - Saved at the **same JPEG quality** as the original (estimated from the file), keeping its
    **dpi and metadata**. Rotate / mirror are **lossless** whenever the image size allows.
- **Batch rename** with a live preview: pattern (`{name}`, `{folder}`, `{n}`), counter, find /
  replace (also regular expressions), letter case. Clashing names block the rename; nothing is
  ever overwritten; Undo puts the old names back.
- **Merge parts**: `123_1` + `123_2` → a new folder `123`, files renamed the Striplight PRO way
  (`73_524_0_0_123_1_00001.jpg` → `73_524_0_0_123_00001.jpg`) and numbered continuously.
  Empty part folders can be deleted afterwards (with an optional confirmation). Undo moves everything back.
- Ten themes (Graphite, Daylight, Midnight, Darkroom, Sepia, Forest, Strawberry Milk, Lavender Dream, Mint Macaron, Midnight Orchid) and a neutral viewing background (grey by default) so scans are judged without a tint, English / Polish.

## Layout

| Path | |
|---|---|
| `src/Loupe.Core` | Engine: TurboJPEG (scaled decode, lossless transforms), tone, edit + backups, thumbnails, rename, merge, safe mover. No UI references, unit tested. |
| `src/Loupe.App` | WPF + WebView2 shell; the UI is HTML/CSS/vanilla JS in `wwwroot/`. |
| `tests/Loupe.Core.Tests` | xUnit tests (`dotnet test tests/Loupe.Core.Tests`). |
| `tools/` | `pl_source.py` writes `wwwroot/i18n/pl.json`; `i18n_check.py` finds untranslated strings. |

The Windows CI build (`.github/workflows/windows-build.yml`) runs the tests, publishes a
single self-contained `Loupe.exe` and smoke-tests the real app.

Translations: English text is the key; your own fixes go into `%APPDATA%\Loupe\lang\pl.json`
(Settings → Edit translation) and survive updates.
