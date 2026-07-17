# Widget Subscription

A Windows notification-area (system tray) utility that shows how much of an AI
subscription's usage limits remain. The MVP covers Claude Code only; other
providers are a later effort.

## Language

**Provider**:
An AI subscription whose usage limits the widget reads. Claude Code (Anthropic)
is the first and only MVP provider.
_Avoid_: vendor, service

**Limit**:
A single quota window with a remaining amount and a reset time. Claude Code
exposes three: the 5-hour rolling session limit, the weekly all-model limit, and
the weekly Fable 5 limit.
_Avoid_: quota (use only for the underlying allowance), cap

**Fable 5**:
Anthropic's flagship model (`claude-fable-5`), a Mythos-class model above Opus,
released June 2026. Relevant because it carries its own weekly limit, separate
from the all-model weekly limit.

**Headroom**:
The remaining amount in a Limit before it is exhausted, shown as a percentage.
_Avoid_: balance, remaining quota (use "headroom")

**Reset**:
The moment a Limit's window refreshes and headroom returns. The 5-hour limit
resets on a rolling window; the weekly limits reset on a fixed schedule.

**Tray widget**:
The whole utility: an icon in the Windows notification area that conveys state
at a glance and expands to a panel showing every Limit.
_Avoid_: taskbar widget (the taskbar has no public embedding API), applet
