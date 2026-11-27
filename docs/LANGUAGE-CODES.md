# One language code inside the client

Inside the plugin exactly one language code is spoken: ISO 639-2, three letters, upper case — `ENG`, `GER`, `ARA`. Every spelling that differs is translated at the edge where it appears, and nowhere else. F-M557 has said since November that the store carries ISO and never the code of a source; what was missing was the reach, because three places produced language pairs and two used the source spelling. The rule applies to the upload pipeline, the download pipeline, the store and every source adapter; the concept runs in 12.3.8.87.

## Who speaks which form

| Place | Form | Example |
| --- | --- | --- |
| library client | ISO 639-2/T, lower case | `deu`, `ces`, `nld`, `fra`, `ron`, `ell`, `msa`, `zho`, `ara` |
| plugin store | ISO, upper case | `ENG` |
| a source | its own code | SubDL `en`, `de`, `pt`; tvsubtitles and moviesubtitles take names |
| the log | ISO with variant | `ENG HI`, `ENG non-HI` |
| the sidecar name | ISO 639-2/T, lower case | `Movie.deu.01.srt` |
| the Matroska tag | the old spelling | `ger`, `fre`, `chi` |

Jellyfin's own canonical form is the T form: its `GetLanguageCode` returns it, which is why the API reports `deu` for a file named `.de.srt`. The older bibliographic form (`ger`, `cze`, `dut`) is only ever an input and is translated on reading, so no stored row and no file name becomes unreadable. Jellyfin accepts two letters, the T form and the old spelling alike, because its file name reader checks the token against the two-letter code, both three-letter codes and the display name; the choice was free and fell on the form the client speaks. Files written before that decision keep their two-letter names and stay readable.

## The container tag stays behind

The Matroska tag written into a media file keeps the old spelling. Containers carry `ger`, `fre`, `chi` in practice and ffprobe prints them back that way, so rewriting tags across the library would be a change with no reader to gain. It is a seam like the sources, and it is named as one.

## Where the list comes from

The list is generated from the official ISO 639-2 list of the registration authority: every code with its bibliographic form, its terminological form, its two-letter form and its English name. The generator is `scripts/tools/gen-language-table.py`, the fetched file is kept beside it so the build needs no network, and the generated class `LanguageTable` says in its header where it came from. That list holds 486 codes, 183 of them with a two-letter form and 20 with two different three-letter forms; both facts are properties of the standard, not of this plugin.

## Three kinds of entry

A language with a two-letter form translates in both directions: `fij` becomes `FJ`, `ger` becomes `DEU`, and `DE` becomes `ger` again for a container tag. A language without one, such as Achinese or Hawaiian, has no code at a source that speaks two letters, and that is an answer, not a failure. Four codes name no language at all: `und` undetermined, `mul` multiple, `mis` uncoded, `zxx` no linguistic content; they are treated as what they are and never as a language wish.

## Where a source keeps its own list

That list wins, and each exception states its reason in the code: Filipino is `TL` because the source files it under Tagalog, and Norwegian stays one language although the standard splits Bokmål and Nynorsk.

## Where translation happens

Translation is allowed in exactly four places, and all four are boundaries. A request to SubDL is built in `MapToSubdl`, a form for tvsubtitles or moviesubtitles is filled in `MapToSubdlName`, an upload file name is assembled because other people read it, and a verdict returns from a source. Everywhere else — in every comparison, in every store key and in every signature — the ISO form stands alone.

## What went wrong

Measured on 27.11.2026: the library reported eight episodes of Rings of Power season 3 with 38 to 39 subtitle streams each over 33 languages, English among them, so a pack looked possible by that measure. The plan then refused every one of its 40+ language pairs with the same sentence, *neither the store nor the files of every aired episode carry this language*. Both witnesses were broken the same way and neither lied about the files: the store answered `ENG`, the file scan answered twice in `EN`, and the comparison asked whether the stream's source code equals the pair's code, so `en` never equalled `ENG`. The scan also read the stream list off the item object, which carries no media sources for a season read as folder children — the pitfall F-M468, fixed in one place and left standing in another. The result was a stored refusal, signed with the files' signature, which would have survived every further run.

## The repair

Four cuts inside the pack plan. The languages of a file are asked by id, not off the object, and named in ISO. The pack pairs of a single episode are named in ISO as well. The comparison runs ISO against ISO through one helper and is deliberately strict, so a stream without any language is never a match. The variant stays what it was, with `HI` and `non-HI` as two pairs, never one. The older helper treats an empty side as equal, which is right when a source is compared with us and wrong here.

## What it costs

Verdicts of the pack phase are keyed by the language mark, so a season whose pairs were spelled `EN` now speaks `ENG` and its old verdict no longer matches; it is examined once more, which costs one look at the files and no extraction. It does not fix the second reason the same season had no pack: its eight episodes sit in two releases, seven from one and one from another, and a pack carries one release. That is a rule of its own and a deliberate one, because sameness matters inside the pack.

## How it is checked

T437 in the `endpoints` run holds four checks. The three spellings `en`, `eng` and `EN` meet in one form. The languages of a file are asked by id and named in ISO. The plan carries ISO everywhere and compares ISO against ISO, and the comparison is strict, so an unlabelled track never answers for a language.

## Where the rules live

[SPECIFICATION.md](SPECIFICATION.md), F-M557, F-M789, F-M790, F-M797 and F-M798, with the pitfall F-M468. Guarded by `endpoints`.
