# Gates

A gate is a question the plugin asks before it spends anything, and its answer is a verdict, not an
error. The extraction divides the gates in two: before it the plugin reads stream lists, paths and
stored rows, which costs nothing, and after it every answer is about what the text says.

## The order

| When | Gate | Decides at | Written as |
| --- | --- | --- | --- |
| before | season complete | every aired episode is in the library | one line per pair |
| before | pack per source | no source of the slot still lacks the pack | one line per pair |
| before | open source | no refusal stands for this signature | the old row stands |
| before | verdict signature | no "no pack" verdict for this signature | the old row stands |
| before | text track of the language | store or files carry it in every aired episode | pack verdict |
| before | one quality, one frame rate | exactly one of each across the aired episodes | one line per pair |
| before | episode membership | one track of the language is not refused | pack verdict |
| after | size floor | 2048 characters | track row, `rejected` |
| after | cue count | 30 cues | track row, `rejected` |
| after | SRT structure | every cue above 100 ms and below 600000 ms, starts never going backwards | track row, `rejected` |
| after | sync | last cue ends at most 300000 ms after the runtime, span at least a tenth of it | track row, `rejected` |
| after | language | the detected language is the tag | new language, or a refusal in a pack |
| after | origin | the origin is unknown | track row, `rejected` |
| after | duplicate | the content hash is unknown | track row, `rejected` |

## Seven questions before the extraction

A pack asks seven questions in this order and none of them starts ffmpeg. Is the season complete on
disk? Does a source of the slot already hold the pack, where a source that does not carry the
language is no missing source? Does a stored refusal still stand for these files, and does a stored
"no pack" verdict? Do the store or the files carry the language in every aired episode? Do all aired
episodes share one quality and one frame rate? And can every episode join, with a track of that
language that was not already refused? The single path asks whether the item is settled, with no
source still wanting a pair, and whether a track is parked after an extraction failure.

## The signature that keeps a verdict

Both stored gates hang on one signature: the aired episode numbers plus size and write time of each
episode file. A new episode, a re-encoded file or a deleted one changes it, and the old verdict stops
applying. The pack reads it once per pair and hands it to both gates, so neither unpacks anything for
a pair that is already closed, and a pass that ends here writes no new verdict.

## After the extraction

The size floor is 2048 characters and always on; the untagged-stream line calls that number bytes
while the code counts characters. Above it the cue count gate demands 30 cues, switchable per
direction and on by default. Then the structure: the timings must parse, the starts must not go
backwards, and every cue must last longer than 100 ms and shorter than 600000 ms. The lower bound
cannot fire on text that went through the canonical form, which drops every cue of 100 ms or less
first. The sync gate puts the cue span beside the runtime: the last cue may end at most 300000 ms
after it, and the cues must cover a tenth of it. The language gate compares the detected language
with the tag; a single upload goes up under the detected language, while a pack refuses the member,
because it carries one language and cannot claim another.

## The origin and the duplicate

Before any source is asked, the origin gate reads the provenance from the store by content hash. A
text that came from a download goes nowhere, neither back to its own source nor to the other one. An
unknown origin may go everywhere, and then only the switch of that source and its mark on the pair
decide. The duplicate gates ask the content: a hash the store recorded after a site called the text
already published is `duplicate-remote`, a hash the store knows at all is `duplicate-content`.

## After a refusal: the report

A refusal in the download direction can leave the plugin, as a report to the site that delivered the text.
The report needs two things and stays home without either: the foreign identifier of the candidate and a
finding — the bare measured defect. It is filed once, in the run that observed it, and the stamp on the row
is what keeps a second sighting from happening (`F-M687`).

The switch belongs to the source card, not to the download section, and a source without its own value
falls back to the global one (`F-M747`). Our own text is never reported, because it never has a foreign
identifier to report against. The findings are queued and flushed at the end of the run; an answer that
never comes puts the report back for the next run instead of ending the run (`F-M592`, `F-M450`).

That is the one switch that must be off in a test bench: a false report reaches a stranger's entry and
cannot be taken back. The count of them sits under the Auto-Sync row of the page, because the fit is what
measures the file.

## What a report says

The comment is one sentence about the bare finding and a blank line under it. The sentence names the package,
the release and, where the finding is not about the listing, the language: `Package <id> ('<release>', <LANG>):
no usable cues.` Three forms carry the exceptions. When the language is the defect the package stands alone:
`Package <id> ('<release>'): listed as <LANG>, no usable cues.` When an empty fetch is the defect the language
drops and the bytes stand in: `Package <id> ('<release>') downloads with <bytes> bytes.` When the package held
several files, the sentence ends in the name of the one the gate measured, e.g. `… cue span 2508s vs. runtime
1254s ('The.Middle.S03E01.srt')`. The uploader would otherwise guess which member is broken, and the name is
cut off before the finding is classified, so it never changes the category. Under it stands one attribution
line for every report: the plugin with its version and the account that files it.

The kind travels in neutral words and the engine makes the site's category out of it. Five kinds exist —
missing, unplayable, wrong content, wrong language, other — and they collapse to three categories at the site:
a missing or unplayable file is no subtitle, a wrong content or a wrong language is wrong release information,
and everything else is other. The accepted report is logged as `reported to <source> (<category>) for subtitle
<nid>`.

## What is written, and what is not

A failed extraction is no verdict: when a whole ffmpeg pass over a pack member fails and the retry
track by track rescues nothing, the pack stores nothing and says so in the log, so the pair stays
open. Every other refusal is written in one place, which writes exactly one row per skip: `rejected`
with the reason, or `uploaded` for the sources that demonstrably hold the content, which leaves the
other source open. Only a quality refusal counts as a QA failure in the statistics. A pack refusal is
written by the pack itself as `no-pack` with the signature it was made for, and a source's own no is
remembered against that content, so the next run does not ask again while the text stays the same.

## Where the rules live

[SPECIFICATION.md](SPECIFICATION.md), §3.1 and §3.3: F-M13, F-M14, F-M15, F-M16, F-M17c, F-M17x,
F-M185, F-M298, F-M375, F-M416, F-M433, F-M442, F-M487, F-M488, F-M500, F-M539, F-M577, F-M595,
F-M600, F-M611, F-M647, F-M650, F-M659, F-M687, F-M741, F-M747, F-M450 and F-M592.
