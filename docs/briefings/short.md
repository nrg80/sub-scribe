# Sub Scribe in one page

**What it is.** A free plugin for the media server Jellyfin. It finds the subtitle that is missing for
a film or an episode and saves it next to the video file, so Jellyfin shows it. If you switch it on, it
can also give a subtitle you already have to one of the subtitle sites it works with: SubDL,
tvsubtitles.net, moviesubtitles.org.

**What you need.** A free TMDb API key from <https://www.themoviedb.org/> — that is the only mandatory
entry. For SubDL you additionally need a SubDL account with its key and your login; without it that
site simply drops out of the chain.

**Installing.** In Jellyfin: Dashboard → Plugins → Repositories, add
`https://raw.githubusercontent.com/nrg80/sub-scribe/main/manifest.json`, then install **Sub Scribe**
under Plugins → All and restart Jellyfin.

**How it behaves.** It runs on its own schedule, checks language, text amount and length before it
saves anything, and leaves a plain line in the log for every run. Uploads are off by default and are
switched per site. Your keys and logins never leave your server and never appear in the log.

**Help.** Issues for anything broken: <https://github.com/nrg80/sub-scribe/issues>. Questions:
<https://github.com/nrg80/sub-scribe/discussions>. In a report put the plugin version, what you
expected, what happened, and the matching log lines — never a key, a password or your e-mail address;
"the key is set" is enough.

**More.** The project page is <https://github.com/nrg80/sub-scribe>: the README explains the parts in
order, and `docs/SPECIFICATION.md` in that repository is the full account.

**Where this sheet lives.** It has an address, so a copy of it can always be traced back and stays
current: <https://raw.githubusercontent.com/nrg80/sub-scribe/main/docs/briefings/short.md>, readable as a
page at <https://github.com/nrg80/sub-scribe/blob/main/docs/briefings/short.md>. When this text is
passed on, that address travels with it.
