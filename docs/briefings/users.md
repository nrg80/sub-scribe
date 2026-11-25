# Sub Scribe for users — what it is, and how to reach the person who builds it

This sheet is written for someone who wants to **use** Sub Scribe, not to change its code. It is part
of the project at <https://github.com/nrg80/sub-scribe>.

## In one paragraph

Sub Scribe is a free plugin for the media server **Jellyfin**. It keeps subtitles in order. It finds
the subtitle that is missing for a film or an episode, and saves it next to the video file so Jellyfin
shows it. Only if you switch it on, it can also give a subtitle you already have to one of the
subtitle websites it works with. It talks to three sites: **SubDL**, **tvsubtitles.net** and
**moviesubtitles.org**. You install it once, fill in a handful of settings, and after that it runs on
its own schedule.

## Who it is for

For people who run their own Jellyfin — a small home server, a NAS, a Raspberry Pi — and who are tired
of hunting for subtitles by hand for every new episode. It is not a streaming service and needs no
subscription. It is published under the GPL-3.0 licence, so it stays free and its source is public.

## What it does for you

- **Fills gaps on its own.** It looks through the libraries you pick, notices which film or episode has
  no subtitle in your language, and fetches one.
- **Three sites instead of one.** If the first site has nothing, the next one is asked. You decide the
  order.
- **Checks before it saves.** A fetched subtitle must match the video's language, carry enough text and
  fit the video's length, otherwise it is thrown away instead of leaving you with a file that does not
  match.
- **Gives back, if you want.** Embedded subtitle tracks can be offered to the sites — off by default,
  switched per site, and never automatically for something the plugin itself downloaded.
- **Tells you what it did.** Every run leaves a plain line in the Jellyfin log, and the settings page
  shows why something was skipped.

## What it does not do

It does not translate subtitles, it does not touch your video files, and it does not phone home.
Nothing about your library leaves your server except the search terms a subtitle site needs in order
to answer a query.

## What you need

1. **A free TMDb API key.** This is the only mandatory entry — it is how a title gets recognised. You
   register at <https://www.themoviedb.org/> and copy the key from your account page.
2. **Jellyfin 10.11 or newer**; the plugin declares API version 12.1.0.0.
3. **For SubDL only:** a SubDL account with its API key and your login. The plugin works without it —
   that source simply drops out of the chain.

## Installing

Inside Jellyfin: **Dashboard → Plugins → Repositories**, add

```
https://raw.githubusercontent.com/nrg80/sub-scribe/main/manifest.json
```

then open **Dashboard → Plugins → All**, install **Sub Scribe**, and restart Jellyfin. The other way is
to download the ZIP of the latest release from the releases page and unpack it into a folder under
your Jellyfin `plugins/` directory, then restart.

After the restart the settings sit under **Dashboard → Plugins → Sub Scribe**. Fill in the TMDb key,
pick your libraries and your languages, save — the plugin can work from there. Uploads stay off until
you switch them on.

## Privacy, in one line

Keys and logins never leave your server and never appear in the log; the settings page shows a stored
key only as dots, and only while you press the eye next to it.

## How to get in contact

Sub Scribe has no office and no support hotline — it is written by one person in his spare time. The
two channels that work are on the project page, <https://github.com/nrg80/sub-scribe>:

- **Issues** for anything broken or missing: <https://github.com/nrg80/sub-scribe/issues>. This is
  where a problem gets fixed, because a report stays readable and searchable for the next person with
  the same problem.
- **Discussions** for questions, setups that behave unexpectedly, and "how do I…":
  <https://github.com/nrg80/sub-scribe/discussions>.

Both are open to anyone with a GitHub account, which is free. There is deliberately no e-mail address
on the project: a public report helps the next user, a private mail helps once.

**What makes a report useful** — the plugin version (the settings page shows it, and the log names it
at start-up), what you expected, what happened instead, and the matching lines from the Jellyfin log.
Never paste an API key, a password or your e-mail address into a report. Saying "the key is set" is
enough.

## Where it stands right now

The current version is **12.3.8.60**, the same number on the stable and on the development line. In the
last few rounds the settings page was reordered so it reads from the inside out. A stored key is shown
as dots instead of text. Both subtitle sites now use the same name and the same explanation for their
*prefer season pack* switch, and the runtime-tolerance field says what it actually measures before it
names its default. The screenshots in the project description show all three tabs in that state.
