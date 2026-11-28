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

Translation is allowed in exactly five places, and all five are boundaries: the sources, the upload file name, a verdict coming back, the settings page, and the offline detector. Everywhere else — in every comparison, in every store key and in every signature — the ISO form stands alone.

## The seams, one by one

Every place that touches a language code, and in which direction it translates. `in → out` is the
direction of the translation, not of the data.

| Seam | Where | What it does |
| --- | --- | --- |
| `Canonical(code)` | `Language/LanguageMapper.cs` | any spelling → ISO 639-2/T; the only way into the house |
| `CanonicalList(list)` | same | a stored list → the same list in T, order kept, duplicates dropped |
| `MapToSubdl(iso)` | same | T → the two-letter code a SubDL request carries |
| `MapToSubdlName(name)` | same | T → the language name a SubDL form field expects |
| `KnowsTwoLetter`, `KnowsThreeLetter`, `IsNoLanguage` | same | questions about the table; they translate nothing |
| `ToSite` / `FromSite` | `Sources/Subdl/SubdlLanguages.cs` | T ↔ SubDL's own code |
| `ToSite` / `FromSite` plus the `Site` table | `Sources/Tvst/TvstLanguages.cs` | T ↔ tvsubtitles' own code |
| `ToSite` / `FromSite` plus the `Site` table | `Sources/Msub/MsubLanguages.cs` | T ↔ moviesubtitles' own code |
| `Voice(key)` | `Sources/SourceLanguages.cs` | picks the bridge of a named source, so a caller never names one |
| `IsoLanguage(code)` | `Sources/SourceContract.cs` | a value at the boundary → T, or empty; never null |
| `MapLanguages(codes, map)` | `Sources/SourceContract.cs` | our codes → the codes of one source, deduplicated |
| `CandidateLanguage(siteCode, map)` | `Sources/SourceContract.cs` | a source's code → T for the store |
| `DetectLanguage(content)` | `Qa/QaGates.cs` | the library's 639-1 → T (F-M802) |
| `CorrectedLanguage`, `IsFamilyMatch`, `LangFamilyMap` | `Qa/QaGates.cs` | a comparison; both sides are translated first |
| `Canonical(tag)` | `Qa/AudioTrackChoice.cs` | a Matroska audio tag → T |
| the name token | `Registry/SidecarNaming.cs` | T ↔ the lower-case token in `<name>.<lang>.<slot>.srt`, both ways |
| the target list | `Configuration/configPage.html` | 57 two-letter wishes → T when the setting is read |
| the table itself | `scripts/tools/gen-language-table.py` | the official list → `LanguageTable`; `scripts/iso639.py` is the helper beside it |

The one translator is `LanguageMapper`. It is the only function that turns an arbitrary spelling into the
house form, and the table it uses is generated, so it knows 183 two-letter and 486 three-letter codes
without anybody typing them. Everything else in that class is a question about the table or an exit to
one particular source.

The three bridges are the sources, and each owns its own list because a site may file a language
differently than the standard does. `Voice(key)` is the façade over them: a caller asks for the bridge of
a source by its short name and never has to know which class stands behind it. `MapLanguages` and
`CandidateLanguage` are the two directions of the same crossing, one outward for a request, one inward for
a candidate.

The detector is the fifth boundary and the newest one. It speaks ISO 639-1 and cannot be asked for
anything else, so its answer is translated where it arrives (`DetectLanguage`) and the comparison that
uses it translates both sides as well. Before 12.3.8.92 it did neither, and every comparison against a
three-letter tag was false and silent.

One pair stays deliberately outside the family list: Japanese and Chinese. A track tagged `jpn` whose text
is heavy on kanji and short on kana is read as `zho`. An exception would be a two-way door: the same entry
that lets Japanese pass as Chinese lets any Chinese text pass as Japanese. Nobody can tell the two apart
from the text alone. Concept decision: the pair is never treated as relatives, and an
exception would only be justified by a detector that separates the two scripts reliably. The one in use
does not, so no entry is written on the strength of a library that cannot prove it. A `JPN` pack is
therefore not built for a season whose Japanese text the detector reads as Chinese, and that single goes up
under the Chinese code. Everything else is packed, which is what the measurement of 27.11.2026 shows:
32 of 33 languages as one container, `JPN` the only one missing.

Names and the store never translate anything new: the token in a sidecar name is written from the house
form and read back through `Canonical`, and the store, the verdict keys and the signatures carry the house
form as it is. The settings page is the one place where a human types a wish, and the wish is two letters
because a human reads it, so it is translated when the setting is read and never again afterwards.

The rule that holds all of it: only `Canonical` translates **into** the house, every other seam either
leaves it deliberately or comes back through `Canonical`. A function that translates between two foreign
forms is a bug, not a convenience.

## What went wrong

Measured on 27.11.2026: the library reported eight episodes of one season with 38 to 39 subtitle streams each over 33 languages, English among them, so a pack looked possible by that measure. The plan then refused every one of its 40+ language pairs with the same sentence, *neither the store nor the files of every aired episode carry this language*. Both witnesses were broken the same way and neither lied about the files: the store answered `ENG`, the file scan answered twice in `EN`, and the comparison asked whether the stream's source code equals the pair's code, so `en` never equalled `ENG`. The scan also read the stream list off the item object, which carries no media sources for a season read as folder children — the pitfall F-M468, fixed in one place and left standing in another. The result was a stored refusal, signed with the files' signature, which would have survived every further run.

## The repair

Four cuts inside the pack plan. The languages of a file are asked by id, not off the object, and named in ISO. The pack pairs of a single episode are named in ISO as well. The comparison runs ISO against ISO through one helper and is deliberately strict, so a stream without any language is never a match. The variant stays what it was, with `HI` and `non-HI` as two pairs, never one. The older helper treats an empty side as equal, which is right when a source is compared with us and wrong here.

## What it costs

Verdicts of the pack phase are keyed by the language mark, so a season whose pairs were spelled `EN` now speaks `ENG` and its old verdict no longer matches; it is examined once more, which costs one look at the files and no extraction. It does not fix the second reason the same season had no pack: its eight episodes sit in two releases, seven from one and one from another, and a pack carries one release. That is a rule of its own and a deliberate one, because sameness matters inside the pack.

## How it is checked

T437 in the `endpoints` run holds four checks, and T448 in `langcorrect` holds six for the detector (F-M802). The three spellings `en`, `eng` and `EN` meet in one form. The languages of a file are asked by id and named in ISO. The plan carries ISO everywhere and compares ISO against ISO, and the comparison is strict, so an unlabelled track never answers for a language.

## Where the rules live

[SPECIFICATION.md](SPECIFICATION.md), F-M557, F-M789, F-M790, F-M797, F-M798 and F-M802, with the pitfall F-M468. Guarded by `endpoints` and `langcorrect`.
