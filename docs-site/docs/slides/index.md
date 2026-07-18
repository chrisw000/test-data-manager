# Slides

## Engineering Forum deck

**[Open the deck →](engineering-forum.html)** — a ~20-minute introduction to TDM: the
problem, QA test seeding, natural keys and the identity contract, the editor helper, CI
operation, reporting, and statistical generation.

The deck is one self-contained HTML file, dogfooding TDM's own report posture:

- Opens from `file://` with **zero network requests** — safe on any podium laptop.
- Keyboard navigation: **← →** to move, **Home/End** to jump, **S** toggles speaker
  notes (every slide carries the takeaway, the demo fallback, and the docs page that
  substantiates its claims).
- Print it for a PDF hand-out — one slide per page; toggle notes on first to include
  them.
- The identity mini-explorer on slide 7 derives UUIDv5 ids live and self-verifies
  against the frozen `Tdm.Identity` contract vector on load.

The deck is written from the same
[messaging spine](https://github.com/chrisw000/test-data-manager/blob/main/docs/wave-5/messaging.md)
as this site's home page, so the two cannot tell diverging stories.
