# The arrival run

An arrival is a file that appeared while the plugin was watching: an episode or a movie that finished copying,
a subtitle dropped into a folder. The arrival run is the short path from that event to a finished piece of work,
and it is the only path that knows what arrived. Everything else — a scheduled run, a manual run, a recovery run
— knows nothing and therefore has to look at everything.

The watcher does not start work itself. It hands the changed items to a collector, and the collector waits until
nothing more comes. What the collector gathered becomes a cycle of its own, in both directions, because an
arrived episode usually needs a subtitle and may carry text worth uploading.

The ids of the collected items travel with the cycle and bound it. The dispatcher passes them to the seeder
(`F-M233`), and the seeder skips every item outside that set (`SkipForArrivalScope`). The libraries of the changed
items travel along as a second bound, so the id set and the library set describe one and the same window and
neither can widen the other.

An arrival cycle cannot widen to a full scan. If the collector is empty it does not run at all and logs `no
arrivals collected — no run`; the full-coverage path stays reserved for the other three triggers.

Arrivals that come in while a cycle is already running are not lost and do not interrupt it. They are latched with
`Event during cycle — next seed round will include it`, and a follow-up trigger fires 90 seconds after the cycle
that drained the collector, running on exactly those latched ids. That is the chain the operator asked for: the
arrived files, plus the ones that arrived during the arrival.

Full coverage belongs to the scheduled run, the manual run and the recovery run. Those three pass no id set, scan
every selected library, and are the net that catches what an arrival could not: items that arrived while the
plugin was down, files whose earlier run was refused, and every refetch that fell due. A manual run is the
operator's way of forcing that net, which is why a button press and a scheduled fire behave the same way.

The gates inside the pipelines are untouched by the scope. A narrow cycle still refuses a file whose pair is
already uploaded, still runs the QA gates and still respects the source switches — the scope only decides which
items are read, never what happens to them.

The difference in cost is measurable. A full scheduled cycle spent 69 minutes in its pack phase before it reached
the first of 1,477 items. An arrival cycle has in front of it what arrived: eight episodes when eight episodes
arrived.

What it cannot do: it cannot see a file that appeared while the plugin was not running. Neither can it repair a
pair that was booked under an older language form. A narrow cycle and a full cycle fail on that alike, and only a
migration of the stored pairs fixes it.

[SPECIFICATION.md](SPECIFICATION.md), F-M189, F-M233, F-M311, F-M331. Guarded by `endpoints` (T436 runs and
triggers) and the dashboard structure test.
