# How the documentation is written

This page says who reads which file and how a paper is written. It exists because the first version of
the four way-papers read like a manual without a speaker, and nobody wanted to read it.

## Who reads what

The README is for someone who has never heard of the plugin. A paper is for someone who wants to
understand one subject from beginning to end, and it has to work without opening the code. The
specification is a rule catalogue for review, and a paper never looks like one.

## One topic, one paragraph

Each section of a paper holds exactly one paragraph. There is one introduction and then the subject
starts. No second and third introduction, no worked example, no summary of what was just said.

## The rules

Write whole sentences with a subject and a verb, one idea per sentence and under 45 words. Say the
concrete thing: the ZIP, the episode, the source, not the container, the slot, the seam. Stay in the
active voice and give numbers instead of adjectives. Only write what the code does, and quote a log
line exactly or not at all. Keep the requirement numbers in one paragraph at the end.

## The shape of a paper

A paper about the way a subtitle travels has eight parts: what it is, the switches, the checks before
anything expensive happens, the checks after, how the result is built and handed over, what never
happens, what the log says, and where the rules live. The four papers [SEASON-PACK-UPLOAD.md](SEASON-PACK-UPLOAD.md),
[SINGLE-UPLOAD.md](SINGLE-UPLOAD.md), [SEASON-PACK-DOWNLOAD.md](SEASON-PACK-DOWNLOAD.md) and
[SINGLE-DOWNLOAD.md](SINGLE-DOWNLOAD.md) follow it, and one of them is the model for the next one.

## What is never written

No keys, passwords, tokens, e-mail addresses or account names, and no server address, host name or
port. No path from the operator's own machines, though a path inside Jellyfin's plugin folder is fine.
No invented number, and no claim about a source that was not seen.

## Bad and better

`**Owner: the container built.** One season, one language, one variant, one release.` became `A pack is
one ZIP that carries every episode of one season, in one language and one variant.` And `Neither switch
is a licence to overrule a check.` became `A switch is only a permission, and the checks below decide
whether it happens.`

## The checks that hold this

`readability/check.py` measures a sentence against 45 words, a paragraph against 200 and a list block
against 400. `doc-hygiene/check.py` looks for keys, addresses and operator paths. The `endpoints` suite
holds the shape of the four papers and the links from the README, and [TERMS.md](TERMS.md) fixes the
words themselves.
