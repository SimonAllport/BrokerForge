# How-to video and guide

Makes `docs/guide/BrokerForge How-to Guide.docx` (from `docs/guide/how-to-guide.md`) and the how-to video, the same way
the DataverseToCode videos are made: Playwright drives real pages, captions and a pointer are drawn into the page, and
ffmpeg joins the recordings.

Part 1 (onboarding a REST API) shows real `bf` output in an editor-and-terminal page (`console.html`); part 2 (connecting
an account) records the real dev environment and connect page. Host and org names are replaced with contoso
placeholders, the signed-in user's photo is covered by a letter, and Microsoft's sign-in pages, which show the account,
are hidden behind a plain screen.

```
npm install
node capture.mjs                # real bf draft / validate / generate --dry-run / undeclared filter -> out/capture.json
node signin.mjs a               # once: sign in (MFA included) in the window that opens
node record.mjs --shots         # screenshots -> docs/guide/images/
node record.mjs                 # video -> out/brokerforge-how-to.mp4 (+ .srt subtitles, .json chapter times),
                                # and each part on its own: out/brokerforge-how-to-part1.mp4, -part2.mp4
node build-guide.mjs            # docs/guide/BrokerForge How-to Guide.docx
```

`record.mjs part1` or `part2` does one part. Part 2 runs as the same signed-in user throughout (a second person's
sign-in, password and MFA, can't be automated): it disconnects TicketsAPI off camera, connects as the mock's `alice`, then
again as `bob` to show the rows follow the connected account, disconnects, and finally reconnects as `alice` off camera.
The mock passwords are read from the dev Key Vault with the az CLI and typed into the page, never printed.

The profile lives in `~/.brokerforge/media-profiles/` (outside the repo: they hold sign-in cookies). When a sign-in expires,
`record.mjs` stops with "run node signin.mjs again". Needs ffmpeg and Playwright's Chromium (`npx playwright install
chromium`).

After recording, check a few frames: `ffmpeg -ss <seconds> -i out/brokerforge-how-to.mp4 -frames:v 1 frame.png`, at
chapter times from the `.json`.
