# The daily allowance

One of the three sources counts what a day may spend, and every run has to live inside that count. This paper says which source keeps such an allowance and how the plugin learns what it spent. It then follows each direction through a spent allowance, the switch that governs the day-long stop, the fire that retries after it, and the questions the plugin cannot answer at all.

## Who keeps an allowance

SubDL is the only source that keeps one. Its capabilities promise a quota, and the registry asks the quota task of the source that promises it, not of a name. tvsubtitles.net and moviesubtitles.org carry no account and promise nothing, and their answer is that they keep no quota. A run that asks across all three therefore receives one set of numbers and two refusals, and the refusals are not an error.

## Two numbers, one account

The allowance is two allowances. The answer carries used, limit, remaining and a reset moment for searches and for downloads, and each is judged on its own, because a run can be out of downloads while searches are free. A download spends the download number, and a search spends the search number. Sending spends the search number too, since an upload is a request like a search.

## How the plugin knows what it spent

It does not count; it asks. The plugin keeps no budget of its own, and the per-run hourly bucket that once stood in the search path was removed as the plugin's own invention. The settings page asks the same endpoint for the same numbers, holds them for five minutes and drops them when the key changes, so the page and the run cannot disagree about a number. Every answer of the interface carries rate headers naming limit, remainder and reset, and the refusal is read for them; the client files those values away beside the numbers, and no other code reads them.

## When it asks

Only after a refusal. The allowance is read when a run has already met a 429, on the search, on the file fetch or on the send, and never before a run and never between two items. A read that fails, or that carries no limit for the direction being asked about, is not a yes: the allowance counts as unknown and takes the same road as spent.

## The shapes of a refusal

A 429 is sorted into three shapes. A rate header is the day's allowance and carries its reset. A named overload or a named short trip is a rate limit, retried in place or respaced by the job spacing. A refusal naming neither is an edge case, and the plugin then trusts only the numbers.

## The download side

An arrival run stops at once and leaves the waiting to the scheduled run. With the switch on, every other download run reads the download number: with room left the direction is respaced by the job spacing, and with the allowance spent or unknown it stops for the day. A second hit in the same run stops without arming another. The stop takes one source out of the run, not the run out of the work, so the item stays queued and the direction reports a deferral instead of a green run.

## The upload side

The upload reads the search number, because sending spends requests. An arrival run takes SubDL out of the run and lets the other sources carry on. Otherwise, with the switch on, the same outcomes apply: room left means a respacing after the job spacing, spent or unknown means the day's stop and one fire, and a second hit stops without arming another. A spent allowance takes SubDL out of the run and leaves the item due, and the other sources keep sending. The upload also has a ceiling of its own, five hundred subtitles in an hour, and that ceiling is not the allowance: it arrives as 200 with a refusal in the body, and it takes SubDL out the same way.

## Why a fire at all

Because a stop alone retries nothing. A direction that has stopped waits for its next regular anchor, and that anchor can be a day away; with a fire, the work that was refused gets one attempt as soon as the allowance is back. The fire is one-shot, it belongs to one direction, and it carries the reason it was armed.

## How the moment is diced

The anchor is the site's own reset when the answer or the refusal carries one. A reset in the past, or more than a day ahead, is not used, and a retry hint in seconds stands in for it. Without either, the anchor is the next midnight in UTC, which is where the plugin believes the day turns. On top of the anchor the coordinator rolls thirty to three hundred minutes, drawn freshly for every fire from the cryptographic generator and rolled separately for upload and download. The offset exists because every client of the site returns at the same moment, so the fire lands well after the reset instead of on it. Two other fires carry no dice: the overload fire and the respacing fire keep the job spacing exactly.

## One fire per direction

A direction holds at most one fire, and a second attempt to arm one is refused while the earlier moment stays, whatever its reason. The moment and its reason are written to a file beside the plugin database, so a restart does not lose the retry, and the coordinator reads them back when it starts. The tick that finds the moment passed queues the task of that direction only. When the task is not registered yet the fire stays pending for the next tick, and when the run lock is busy the fire is deferred by the job spacing instead of dropped. A stop cancels the fire of the direction it names.

## What the switch changes

Each direction has its own switch, and both stand on until somebody turns them off. A switch governs one thing: whether a spent allowance earns a fire. Off, the stop is clean and nothing is armed — `Daily limit reached - continue-after-limit is OFF: clean stop, no recovery fire` — and the numbers are not read at all. On, the numbers are read, and the stop either earns a fire or, with room left, a respacing. What the switch no longer does is make the run wait: no path of either direction waits out the reset and retries the item, and the branches that would use such a wait are never reached. It also does not decide a short trip on the search path, where a rate limit with room left is respaced whichever way the switch stands. On the file fetch and on the send the switch is asked first, so off stops there before the kind of refusal is known.

## What it does not know

It does not know the remaining amount before it spends, because it asks only after a refusal, and it cannot tell a spent allowance from one it failed to read. It does not know the plan's numbers at all: neither limit is a constant in the plugin, both arrive in the answer, and a limit that does not arrive is unknown rather than free. It does not know the hourly upload ceiling until the site says so. It does not know what a run will cost either, since one unit is paid per archive and not per subtitle, and an archive already on disk pays nothing. And the numbers are the account's, not the run's: anything else using the same account spends out of the same allowance, and the plugin cannot see who took what.

[SPECIFICATION.md](SPECIFICATION.md), F-M20, F-M49, F-M58, F-M62, F-M65, F-M70, F-M72, F-M116, F-M152, F-M182, F-M206, F-M238, F-M351, F-M369, F-M370, F-M386, F-M625, F-M713, F-M714, F-M733. Guarded by `sources` (F-M733) and `stopfire` (F-M351).
