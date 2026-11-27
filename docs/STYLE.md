# How the documentation is written

This page is the style sheet. It says who reads which file, how a paper is written, and what is not
allowed. It exists because the first version of the four way-papers was written like a manual without a
speaker, and nobody wanted to read it.

## Contents

- [1. Who reads what](#1-who-reads-what)
- [2. The rules for a paper](#2-the-rules-for-a-paper)
- [3. The shape of a paper](#3-the-shape-of-a-paper)
- [4. What is never written](#4-what-is-never-written)
- [5. Bad and better](#5-bad-and-better)
- [6. The checks that hold this](#6-the-checks-that-hold-this)

## 1. Who reads what

Three kinds of text live in `docs/`, and each has its own reader.

- The **README** is for someone who has never heard of the plugin. It says what the plugin is, what it
  needs and how to install it.
- A **paper** is for someone who wants to understand one subject from beginning to end — how a season
  pack is formed, how a download runs. This is what most people read, and it has to be readable
  without opening the code.
- The **specification** is a rule catalogue, for review and for the code. It is allowed to be a list of
  numbered rules, because that is what it is. A paper never looks like that.

A paper repeats no rule. It points at the specification at the end and tells the story in between.

## 2. The rules for a paper

1. Start with a short paragraph that says what the paper is about, in your own words. No `Owner:` line
   and no "Derived document" formula — both were invented here and helped nobody.
2. Write whole sentences with a subject and a verb. One idea per sentence, and stay under 45 words.
3. Say the concrete thing. Write "the ZIP" instead of "the container", "the episode" instead of "the
   slot", "the source" instead of "the seam". If a word needs explaining, explain it once or drop it.
4. Stay in the active voice. "The plugin unpacks the file", not "the file is unpacked".
5. Give numbers, not adjectives: "under 2 KB", "fewer than 30 cues", "0.1 seconds to 10 minutes". A
   reader can argue with a number and cannot argue with "sufficient".
6. Only write what the code does. When something cannot be measured, say that it cannot.
7. Quote a log line exactly, in backticks, and only lines that exist. A made-up line is worse than no
   line.
8. Collect the requirement numbers at the end in one paragraph. A sentence per `F-M` tag reads like
   footnotes and buries the story.
9. Keep a list to short points, and give each point a verb. A bullet is a sentence that happens to
   stand on its own line.
10. Use tables for switches, thresholds and log lines. Use prose for everything else.

## 3. The shape of a paper

A paper about a way a subtitle travels has eight parts, in this order:

1. What it is, in one paragraph.
2. Which switches allow it.
3. What is checked before anything expensive happens.
4. What is checked after it happened.
5. How the result is built and handed over.
6. What it never does.
7. What the log says.
8. Where the rules live, and which suites guard them.

The four papers [SEASON-PACK.md](SEASON-PACK.md), [SINGLE-UPLOAD.md](SINGLE-UPLOAD.md),
[SEASON-PACK-DOWNLOAD.md](SEASON-PACK-DOWNLOAD.md) and [SINGLE-DOWNLOAD.md](SINGLE-DOWNLOAD.md) follow
this shape. Copy whichever one is nearest to what you are writing.

## 4. What is never written

- No keys, passwords, tokens, e-mail addresses or account names — not even a redacted one.
- No server address, no host name, no port.
- No path from the operator's own machines. A path inside Jellyfin's plugin folder is fine, because
  every installation has it.
- No invented number. Every figure in a paper comes from the code or from a measurement that is
  named in the sentence.
- No promise about a source's behaviour that was not seen. "SubDL refuses a duplicate" is a claim;
  "SubDL refused a duplicate on 20.11.2026" is evidence.

## 5. Bad and better

The left column is what the first version said. The right column is what it says now.

| Bad | Better |
|---|---|
| `**Owner: the container built.** One season, one language, one variant, one release.` | `A season pack is one ZIP that holds every episode of a season in one language, using the same variant for all of them.` |
| `The seventh check asks whether the language has a text track at all, and whether one of those tracks is still open.` | `The last check asks whether the file has a text track in that language at all, and whether one of them is still open.` |
| `Neither switch is a licence to overrule a check.` | `A switch is only a permission: it says that something may go up, and the checks decide whether it really does.` |

## 6. The checks that hold this

`python3 scripts/tests/readability/check.py` measures a sentence against 45 words, a paragraph against
200 and a list block against 400. `python3 scripts/tests/doc-hygiene/check.py` looks for keys,
addresses and operator paths. The `endpoints` suite holds the shape of the four papers and the links
from the README. The words themselves are fixed in [TERMS.md](TERMS.md).
