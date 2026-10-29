# Quick-setup guides

Step-by-step guides, with a picture per step, to install PTCGL Auto Battle Log (PAB) and connect
it to the sites it uploads to. This folder is their single source: tcgkb and any other site show
the guides from here instead of keeping their own copy.

| Guide | Page | For |
|---|---|---|
| `install` | [Install PAB](install.md) | Saving battle logs on your PC, nothing uploaded |
| `tcgkb` | [Get started in 5 minutes](tcgkb.md) | Uploading matches to the [tcgkb](https://tcgkb.app) ranking |
| `pokematch` | [Upload your matches to Pokematch](pokematch.md) | Adding matches to your [Pokematch](https://pokematch-vslogger.onrender.com/) logs |

Every guide is in English, Spanish, Portuguese, French, German, Italian, Japanese, Korean and
Chinese. The app's own labels ("Set API token...") stay in English in every language, as in the app.

## Showing the guides on another site

Read [`steps.json`](steps.json) from its raw URL:

```
https://raw.githubusercontent.com/manucruzleiva/ptcgl-auto-battlelog/main/docs/quick-setup/steps.json
```

and each picture from `baseUrl` + `imagePath`, with `{lang}` and `{step}` filled in, for example

```
https://raw.githubusercontent.com/manucruzleiva/ptcgl-auto-battlelog/main/docs/quick-setup/images/es/tcgkb-connect.png
```

`main` is what the latest work says, which can be ahead of the released app. Raw GitHub URLs are
cached for a few minutes; a site that builds its pages can also fetch the file at build time and
keep a copy, or cache it for an hour or so.

### Format (`schemaVersion` 1)

```jsonc
{
  "schemaVersion": 1,                   // changes only when an existing field changes meaning or goes away
  "languages": ["en", "es", ...],       // every text below has all of these
  "defaultLanguage": "en",
  "baseUrl": "https://raw.githubusercontent.com/.../docs/quick-setup/",
  "imagePath": "images/{lang}/{step}.png",  // relative to baseUrl; 960×540 PNG
  "ui": { "progress": {...}, "steps": {...}, "prev": {...}, "next": {...}, "restart": {...} },
  "guides": [
    { "id": "tcgkb", "title": {...}, "subtitle": {...}, "steps": ["tcgkb-account", "download", ...] }
  ],
  "steps": {
    "download": {
      "phase": {...},     // optional: a heading shown before this step ("On your PC")
      "short": {...},     // label for a list of steps
      "title": {...},
      "body": {...},
      "tip": {...},       // optional: a smaller note under the body
      "cta": { "href": "https://...", "label": {...} },  // optional: a button
      "alt": {...},       // the picture's alt text
      "appText": ["..."]  // the app's own texts this step quotes (checked by the build)
    }
  }
}
```

Every `{...}` text is an object by language, `{"en": "...", "es": "...", ...}`. `ui.progress`
has `{n}` and `{total}` to fill in. Guides share steps (`download`, `install`, `play`): a step
looks the same in every guide that has it. New fields, steps, guides or languages can appear in
any version: ignore what you do not know.

## Changing a guide

1. Edit `steps.json` (texts, steps, guides) and, for a picture, `tools/quick-setup/illustrations.html`
   (drawings) or `tools/quick-setup/illustration-text.json` (the callouts in each language).
2. Write the English pages: `python tools/quick-setup/make_pages.py`
3. Draw the pictures again (needs Node.js and the network; Japanese, Korean and Chinese need the
   Noto Sans CJK fonts; with [pngquant](https://pngquant.org/) on PATH, `pip install pngquant-cli`,
   the pictures come out about 4 times smaller):

   ```
   npm install --no-save playwright && npx playwright install chromium
   node tools/quick-setup/render.mjs            # or only some steps: render.mjs tcgkb-connect play
   ```

4. Commit the changed `steps.json`, pages and images.

The build (`build.sh`, `build.ps1`) runs `tools/quick-setup/check.py`: it fails when a text is
missing in a language, a picture is missing, a page is stale, or a step quotes an app text
(`appText`) that the tray app no longer has. Renaming a menu item therefore means updating the
guides in the same change.
