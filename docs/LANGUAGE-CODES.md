# One language code inside the client

**Status:** concept, implemented in 12.3.8.77 (F-M789).
**Applies to:** upload pipeline, download pipeline, store, every source adapter.

## 1. The rule

Inside the plugin exactly **one** language code is spoken: the ISO 639-2 form, three letters, upper case —
`ENG`, `GER`, `ARA`. Every spelling that differs from it is translated **at the edge where it appears**, and
nowhere else.

The rule is not new. F-M557 has said since November that the store carries ISO and never the code of a
source. What was missing was not the rule but its reach: three places produced language pairs, and two of
them quietly used the source spelling.

## 2. Who speaks which spelling

- **The library client** delivers three letters, lower case: `eng`, `ger`, `ara`. This is the form every
  stream list carries, and it is the reason the rule can be kept at all.
- **The plugin store** carries ISO, upper case: `ENG`. Two rows of the same language must never sit side by
  side in two spellings; that is what the canonical form is for.
- **A source** speaks its own code. SubDL uses two letters (`en`, `de`, `pt`). tvsubtitles and
  moviesubtitles take names. A file name may carry either.
- **The operator** reads the log. There the language appears as the ISO form with its variant, `ENG HI` or
  `ENG non-HI`, so that one glance compares.

## 3. Where translation happens

Translation is allowed in exactly four places, and all four are boundaries:

1. Where a request to **SubDL** is built (`MapToSubdl`).
2. Where a **form** for tvsubtitles or moviesubtitles is filled (`MapToSubdlName`).
3. Where an **upload file name** is assembled, because the file name is read by other people.
4. Where a **verdict comes back** from a source and has to be recognised as ours again.

Everywhere else, and in particular in every comparison, in every store key and in every signature, the ISO
form stands alone.

## 4. What went wrong

Measured on 27.11.2026, Rings of Power season 3. The library reports eight episodes with 38 to 39 subtitle
streams each, spread over 33 languages — English among them. A season pack was possible by that measure.

The pack plan refused **every one** of its 40+ language pairs with the same sentence: *neither the store nor
the files of every aired episode carry this language*. Both witnesses were broken in the same way, and
neither of them was lying about the files.

- The plan collected its pairs from three producers. The store answered in ISO (`ENG`), the file scan
  answered twice in the source spelling (`EN`).
- The comparison that should have confirmed a pair asked whether the stream's **source code** equals the
  pair's code. `en` never equals `ENG`, so a pair born in the store could not be confirmed by any file,
  ever.
- The file scan itself read the stream list **off the item object**. A season that the pack phase reads as
  folder children carries no media sources on its object, so that list was empty — the pitfall F-M468
  documented for the same phase, fixed in one place and left standing in another.

The result was not a missing pack alone. It was a stored refusal, signed with the files' signature, which
would have survived every further run without anyone looking again.

## 5. The repair

Four small cuts, all inside the pack plan:

1. The languages of a file are asked **by id**, not off the object, and named in ISO.
2. The second producer, the pack pairs of a single episode, is named in ISO as well.
3. The comparison runs **ISO against ISO** through one helper, and it is deliberately strict: a stream
   without any language is never a match. The older helper at hand treats an empty side as equal, which is
   right when a source is compared with us and wrong here.
4. The variant stays what it was: `HI` and `non-HI` are two pairs, never one.

## 6. What it costs, and what it does not fix

Verdicts of the pack phase are keyed by the language mark. A season whose pairs used to be spelled `EN` now
speaks `ENG`, so its old verdict no longer matches and the season is examined **once** more. That costs one
look at the files, no extraction, and it is the price of one spelling.

It does **not** fix the second reason the same season had no pack: its eight episodes sit in two releases,
seven from one and one from another, and a pack carries one release. That is a rule of its own, and a
deliberate one — sameness matters inside the pack, not around it.

## 7. How it is checked

T437 in the `endpoints` run, four checks. The three spellings `en`, `eng` and `EN` meet in one form. The
languages of a file are asked by id and named in ISO. The plan carries ISO everywhere and compares ISO
against ISO. And the comparison is strict, so an unlabelled track never answers for a language.
