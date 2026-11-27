# Season pack download

A season pack is one archive from a site that carries many episodes of one season in one language. The
plugin takes it once, unpacks what it needs and books the result.

## The switches

`Download enabled` decides whether the site is asked. `Prefer season pack` (`preferSeasonZip`) decides
whether a site that offers archives is asked for one; on the tvsubtitles side it switches the season
page on, and the SubDL side is ordered by the run itself.

## The order of a run

The run collects candidates, asks the sites, and ranks what comes back: an archive that carries the
wanted episode and already lies in the cache comes first, then a cached single, then an archive not yet
cached, then a plain single. The chosen archive is fetched, one member is unpacked, the subtitle is
written beside the video and the result is booked.

## Checks before the fetch

The site has to carry the language, the archive has to belong to the wanted season, the file must not
be present already, and a site that has spent its daily quota is not asked.

## Checks after the fetch

The member is unpacked, named like any other subtitle, and judged: 100 bytes, parseable SRT, 30 cues,
the language as verification and not as correction, and a last cue that fits the runtime. A failed gate
saves nothing, and a later member of the same archive can still pass.

## What is counted

One archive counts once, under *Season packs downloaded*; the further episodes that come out of it
count nothing. The archive itself stays in the cache, which [CACHING.md](CACHING.md)
describes.

## Two things worth knowing

The preference works differently on the two sides. tvsubtitles asks for the season archive itself, so the
archive is fetched before the episode page in the automatic run too. On the SubDL side the preference only
sorts packs to the front when the run asks for an archive, and the automatic run never does; there the
order comes from the ranking above. And the run marks an archive as counted before it knows whether the
save succeeded, so a first save that is refused spends the one count of that archive.

## What a pack download never does

It never invents a language, it never fetches the same archive twice in one run, it never overwrites a
file, and it never uploads anything it downloaded.

## What the log says

`unpack pick S{Season}E{Episode} from pack {Release} → {File}`,
`pack {Release} did not resolve S{Season}E{Episode} from the archive — nothing saved`,
`pack {Release} has {N} files but none for S{Season}E{Episode} — candidate skipped instead of saving the
wrong episode`, `{File} [{Lang}] — QA reject {Release}: {Reason} (no usable file left in this archive)`,
`{File} [{Lang}] — next file of the same archive accepted: {Sibling}`.

## Where the rules live

[SPECIFICATION.md](SPECIFICATION.md), mainly F-M15, F-M16, F-M277, F-M387, F-M450, F-M474, F-M488,
F-M504, F-M663 and F-M759. Guarded by `endpoints`, `seasonpack` and `archivecache`.
