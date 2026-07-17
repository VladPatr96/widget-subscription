# Domain Docs

How the engineering skills should consume this repo's domain documentation when exploring the codebase.

## Layout

This is a single-context repository:

- `CONTEXT.md` at the repo root contains the domain glossary and model.
- `docs/adr/` contains architectural decision records.

## Before exploring, read these

- Read `CONTEXT.md` before naming or changing domain concepts.
- Read ADRs under `docs/adr/` that touch the area being changed.

If these files do not exist, proceed silently. Do not flag their absence or suggest creating them upfront. The `/domain-modeling` skill creates them lazily when terms or decisions are actually resolved.

## Use the glossary's vocabulary

When output names a domain concept—in an issue title, refactor proposal, hypothesis, or test name—use the term defined in `CONTEXT.md`. Do not drift to synonyms the glossary explicitly avoids.

If a needed concept is absent from the glossary, reconsider whether the term belongs to the project. If it represents a real gap, note it for `/domain-modeling`.

## Flag ADR conflicts

If output contradicts an existing ADR, surface the conflict explicitly rather than silently overriding it:

> _Contradicts ADR-0007 (event-sourced orders)—but worth reopening because…_
